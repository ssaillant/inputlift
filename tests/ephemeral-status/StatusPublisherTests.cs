// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Production seam + synthetic Windows files only. No installed state is read.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class StatusPublisherTests
{
    private static int checks;
    private static readonly List<string> errors=new List<string>();
    private static readonly List<object> nativeCases=new List<object>();
    private static void Check(bool condition,string label)
    { checks++;if(!condition)throw new InvalidOperationException(label); }
    private static Exception Refused(Action action,string label)
    { try{action();}catch(Exception error){checks++;return error;}throw new InvalidOperationException(label); }
    private sealed class Mock : EphemeralStatusPublisher.ITemporaryStatus
    {
        internal int Prepares,Replaces,Cleanups;
        internal Action OnPrepare,OnReplace,OnCleanup;
        private EphemeralStatusPublisher.Phase phase;
        public EphemeralStatusPublisher.Phase CurrentPhase{get{return phase;}}
        public void Prepare(){Prepares++;phase=EphemeralStatusPublisher.Phase.WriteTemporary;if(OnPrepare!=null)OnPrepare();}
        public void Replace(){Replaces++;phase=EphemeralStatusPublisher.Phase.Replace;if(OnReplace!=null)OnReplace();}
        public void Cleanup(){Cleanups++;phase=EphemeralStatusPublisher.Phase.Cleanup;if(OnCleanup!=null)OnCleanup();}
    }
    private static IOException Io(int code){return new IOException("Synthetic filesystem error "+code,unchecked((int)(0x80070000u|(uint)code)));}
    private static EphemeralStatusPublisher.Result Pure(Mock file,Func<bool> cancelled,ref long time)
    {
        long[] value={time};
        var result=EphemeralStatusPublisher.Publish(file,cancelled,delegate{return value[0];},delegate(int delay){value[0]+=delay;});
        time=value[0];return result;
    }
    private static void PureCases()
    {
        long time=0;
        var file=new Mock();
        var result=Pure(file,delegate{return false;},ref time);
        Check(result.Published&&result.Attempts==1&&file.Prepares==1&&file.Replaces==1&&file.Cleanups==1,"Uncontended status prepares/replaces/cleans exactly once.");
        foreach(int code in new[]{32,33})
        {
            file=new Mock{OnReplace=delegate{throw Io(code);}};time=0;
            result=Pure(file,delegate{return false;},ref time);
            Check(result.State==EphemeralStatusPublisher.Outcome.Deferred&&result.LastWin32==code,"Only the exact sharing/lock conflict is deferred.");
            Check(result.Attempts==9&&file.Prepares==1&&file.Cleanups==1&&time==200,"One temporary file and at most eight 25ms retry delays are used.");
            Check(result.LastPhase==EphemeralStatusPublisher.Phase.Replace,"Deferred diagnostic names the actual replacement phase.");
        }
        file=new Mock();file.OnReplace=delegate{if(file.Replaces<3)throw Io(32);};time=0;
        result=Pure(file,delegate{return false;},ref time);
        Check(result.Published&&result.Attempts==3&&file.Prepares==1&&time==50,"A released conflict publishes once without creating another temporary file.");
        foreach(int code in new[]{5,2,87,1175,1176,1177})
        {
            var original=Io(code);file=new Mock{OnReplace=delegate{throw original;}};time=0;
            var error=Refused(delegate{EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return 0;},delegate{throw new Exception("Unexpected retry");});},"Non-sharing errors must be fatal.");
            Check(object.ReferenceEquals(error,original)&&file.Replaces==1&&file.Cleanups==1,"Non-sharing error identity/HRESULT survive and cleanup still runs.");
            Check((string)error.Data["InputLift.StatusPublicationPhase"]=="Replace"&&(int)error.Data["InputLift.StatusPublicationHResult"]==original.HResult,"Fatal phase/HRESULT breadcrumbs contain no input data.");
        }
        file=new Mock{OnPrepare=delegate{throw Io(32);}};
        var prepareError=Refused(delegate{EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return 0;},delegate{throw new Exception("Unexpected retry");});},"Writing the new temporary file is not silently deferred.");
        Check(file.Replaces==0&&file.Cleanups==1&&(string)prepareError.Data["InputLift.StatusPublicationPhase"]=="WriteTemporary","Only replacement conflicts enter the retry path.");
        file=new Mock();time=0;
        result=Pure(file,delegate{return true;},ref time);
        Check(result.State==EphemeralStatusPublisher.Outcome.Cancelled&&file.Prepares==0&&file.Replaces==0,"Cancellation before preparation creates no file.");
        bool cancelled=false;file=new Mock{OnPrepare=delegate{cancelled=true;}};time=0;
        result=Pure(file,delegate{return cancelled;},ref time);
        Check(result.State==EphemeralStatusPublisher.Outcome.Cancelled&&file.Prepares==1&&file.Replaces==0&&file.Cleanups==1,"Cancellation after preparation cleans the one owned temporary without publishing.");
        cancelled=false;file=new Mock{OnReplace=delegate{throw Io(32);}};
        result=EphemeralStatusPublisher.Publish(file,delegate{return cancelled;},delegate{return 0;},delegate{cancelled=true;});
        Check(result.State==EphemeralStatusPublisher.Outcome.Cancelled&&file.Replaces==1&&file.Cleanups==1,"Cancellation during a retry delay admits no further replacement.");
        file=new Mock{OnReplace=delegate{throw Io(32);},OnCleanup=delegate{throw Io(5);}};
        var aggregate=Refused(delegate{EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return 1000;},delegate{});},"A failed owned-temp cleanup must remain fatal.") as AggregateException;
        Check(aggregate!=null&&aggregate.InnerExceptions.Count==2&&aggregate.InnerExceptions[0].HResult==Io(32).HResult&&aggregate.InnerExceptions[1].HResult==Io(5).HResult,"Cleanup failure preserves both exact original and cleanup errors.");
        Check((string)aggregate.Data["InputLift.StatusPublicationPhase"]=="Cleanup"&&(int)aggregate.Data["InputLift.StatusPublicationHResult"]==Io(5).HResult,"Aggregate failure identifies the cleanup phase and its actual HRESULT.");
        var cleanupOnly=Io(5);file=new Mock{OnCleanup=delegate{throw cleanupOnly;}};
        var cleanupError=Refused(delegate{EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return 0;},delegate{});},"Cleanup failure after replacement must remain fatal.");
        Check(object.ReferenceEquals(cleanupError,cleanupOnly)&&(string)cleanupError.Data["InputLift.StatusPublicationPhase"]=="Cleanup"&&(int)cleanupError.Data["InputLift.StatusPublicationHResult"]==Io(5).HResult,"Cleanup-only failure preserves its exact error with bounded breadcrumbs.");
        long backward=100;file=new Mock{OnReplace=delegate{backward--;throw Io(32);}};
        Refused(delegate{EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return backward;},delegate{});},"A backward retry clock must fail closed.");
        Check(file.Replaces==1&&file.Cleanups==1,"Clock failure cannot create another attempt or leak its owned cleanup.");
        long slow=0;file=new Mock{OnReplace=delegate{slow+=250;throw Io(32);}};
        result=EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return slow;},delegate{throw new Exception("Retry after window");});
        Check(result.State==EphemeralStatusPublisher.Outcome.Deferred&&file.Replaces==1,"A slow native return does not trigger retries beyond the elapsed window.");
        long overscheduled=0;int waitCalls=0;file=new Mock{OnReplace=delegate{throw Io(32);}};
        result=EphemeralStatusPublisher.Publish(file,delegate{return false;},delegate{return overscheduled;},delegate{waitCalls++;overscheduled+=250;});
        Check(result.State==EphemeralStatusPublisher.Outcome.Deferred&&file.Replaces==1&&result.Attempts==1,"An overscheduled retry delay cannot admit another native replacement after the deadline.");
        Check(waitCalls==1&&file.Prepares==1&&file.Cleanups==1,"An overscheduled retry still cleans its one owned temporary exactly once.");
        long sequence=7;
        EphemeralStatusPublisher.ApplyPublishedSequence(ref sequence,8,new EphemeralStatusPublisher.Result(EphemeralStatusPublisher.Outcome.Deferred,9,32,EphemeralStatusPublisher.Phase.Replace));
        Check(sequence==7,"Deferred publication never renews the actual published progress sequence.");
        EphemeralStatusPublisher.ApplyPublishedSequence(ref sequence,8,new EphemeralStatusPublisher.Result(EphemeralStatusPublisher.Outcome.Cancelled,1,32,EphemeralStatusPublisher.Phase.Replace));
        Check(sequence==7,"Cancellation never fabricates a published heartbeat.");
        EphemeralStatusPublisher.ApplyPublishedSequence(ref sequence,8,new EphemeralStatusPublisher.Result(EphemeralStatusPublisher.Outcome.Published,1,0,EphemeralStatusPublisher.Phase.Complete));
        Check(sequence==8,"Only successful publication commits its next sequence.");
        Refused(delegate{EphemeralStatusPublisher.ApplyPublishedSequence(ref sequence,8,result);},"A duplicate/foreign sequence cannot be committed.");
    }
    private static FileSecurity FixtureSecurity()
    {
        using(WindowsIdentity identity=WindowsIdentity.GetCurrent())
        {
            var security=new FileSecurity();security.SetOwner(identity.User);security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User,FileSystemRights.FullControl,AccessControlType.Allow));return security;
        }
    }
    private static string ReadHeld(FileStream file)
    {
        file.Position=0;using(var reader=new StreamReader(file,new UTF8Encoding(false),false,1024,true))return reader.ReadToEnd();
    }
    private static EphemeralStatusPublisher.NativeTemporaryStatus Native(string target,string text)
    {
        return new EphemeralStatusPublisher.NativeTemporaryStatus(target,text,FixtureSecurity(),delegate {
            if((File.GetAttributes(Path.GetDirectoryName(target))&FileAttributes.ReparsePoint)!=0||
                (File.Exists(target)&&(File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0))throw new InvalidOperationException("Synthetic fixture path changed.");
        });
    }
    private static void WindowsCases(string root)
    {
        const string old="{\"ProgressSequence\":7,\"Synthetic\":true}",next="{\"ProgressSequence\":8,\"Synthetic\":true}";
        string target=Path.Combine(root,"blocked.status.json");File.WriteAllText(target,old);
        string unrelated=target+"."+new string('0',32)+".tmp";File.WriteAllText(unrelated,"Unrelated synthetic temporary");
        var native=Native(target,next);
        EphemeralStatusPublisher.Result result;long sequence=7;
        using(var reader=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        {
            result=EphemeralStatusPublisher.Publish(native,delegate{return false;});
            EphemeralStatusPublisher.ApplyPublishedSequence(ref sequence,8,result);
            Check(result.State==EphemeralStatusPublisher.Outcome.Deferred&&result.LastWin32==32,"Real Windows no-delete-share reader produces Win32 32 through the production seam.");
            Check(File.ReadAllText(target)==old&&ReadHeld(reader)==old&&sequence==7,"Real persistent sharing conflict retains last-good JSON and published sequence.");
            Check(!File.Exists(native.TemporaryPath)&&File.ReadAllText(unrelated)=="Unrelated synthetic temporary","Only the method-created temporary is removed; an unrelated same-prefix temporary remains.");
        }
        nativeCases.Add(new { name="persistent-reader",outcome=result.State.ToString(),result.Attempts,Win32=result.LastWin32,ProgressSequence=sequence,OwnedTemporaryRemoved=!File.Exists(native.TemporaryPath),UnrelatedPreserved=File.Exists(unrelated) });
        target=Path.Combine(root,"release.status.json");File.WriteAllText(target,old);native=Native(target,next);
        FileStream blocked=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);int waits=0;long retryTime=0;
        try
        {
            // Real replacement and locking; a controlled retry clock keeps this
            // release-path assertion independent of cold-filesystem latency.
            result=EphemeralStatusPublisher.Publish(native,delegate{return false;},delegate{return retryTime;},
                delegate(int delay){waits++;if(blocked!=null){blocked.Dispose();blocked=null;}retryTime+=delay;});
        }
        finally{if(blocked!=null)blocked.Dispose();}
        Check(result.Published&&result.Attempts==2&&waits==1&&File.ReadAllText(target)==next&&!File.Exists(native.TemporaryPath),"Real reader release during retry publishes using the same complete temporary.");
        nativeCases.Add(new {name="reader-released",outcome=result.State.ToString(),result.Attempts});
        target=Path.Combine(root,"delete-share.status.json");File.WriteAllText(target,old);native=Native(target,next);
        using(var reader=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            result=EphemeralStatusPublisher.Publish(native,delegate{return false;});
            Check(result.Published&&result.Attempts==1&&File.ReadAllText(target)==next&&ReadHeld(reader)==old,"Real delete-sharing reader permits atomic replacement while its old snapshot remains complete.");
        }
        Check(!File.Exists(native.TemporaryPath),"Successful native replacement consumes its owned temporary.");
        nativeCases.Add(new {name="delete-sharing-reader",outcome=result.State.ToString(),result.Attempts});
        target=Path.Combine(root,"cancel.status.json");File.WriteAllText(target,old);native=Native(target,next);bool cancel=false;retryTime=0;
        using(var reader=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        {
            result=EphemeralStatusPublisher.Publish(native,delegate{return cancel;},delegate{return retryTime;},delegate(int delay){retryTime+=delay;cancel=true;});
            Check(result.State==EphemeralStatusPublisher.Outcome.Cancelled&&result.Attempts==1&&File.ReadAllText(target)==old&&!File.Exists(native.TemporaryPath),"Real retry cancellation keeps the target and removes only its owned temporary.");
        }
        nativeCases.Add(new {name="cancelled-reader-retry",outcome=result.State.ToString(),result.Attempts});
    }
    private static int Main(string[] args)
    {
        string root=null;
        try
        {
            if(args.Length!=2||args[0]!="--fixture")throw new ArgumentException("Use the reviewed fixture launcher.");
            root=Path.GetFullPath(args[1]);string output=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            Check(string.Equals(Path.GetDirectoryName(root),output,StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(Path.GetFileName(root),@"\Afixture-[0-9a-f]{32}\z"),"Fixture root is a fresh direct child of the ignored build directory.");
            for(string cursor=output;!string.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor))
                Check((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)==0,"Fixture ancestry is not a reparse point.");
            Check(!Directory.Exists(root)&&!File.Exists(root),"Fixture root is create-only.");
            Directory.CreateDirectory(root);PureCases();WindowsCases(root);
        }
        catch(Exception error){errors.Add(error.GetType().Name+": "+error.Message);}
        string json=new JavaScriptSerializer().Serialize(new {status=errors.Count==0?"ok":"error",tests=checks,windowsFileCases=nativeCases,
            fixtureDirectory=root==null?null:Path.GetFileName(root),InstalledStateRead=false,InstalledStateChanged=false,
            BluetoothCaptureStarted=false,InputSent=false,RegistryAccessed=false,NetworkAccessed=false,errors=errors});
        Console.WriteLine(json);return errors.Count==0?0:1;
    }
}
