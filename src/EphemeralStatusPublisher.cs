// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Status snapshots only. Durable ownership/consent journals use their original
// fail-closed writer. No input, device, registry, packet or network APIs.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal static class EphemeralStatusPublisher
{
    internal const int RetryWindowMilliseconds=200,RetryIntervalMilliseconds=25,MaximumAttempts=9;
    internal enum Phase { None,Validate,CreateTemporary,WriteTemporary,FlushTemporary,CloseTemporary,Replace,Cleanup,Complete }
    internal enum Outcome { Published,Deferred,Cancelled }
    internal sealed class Result
    {
        internal readonly Outcome State;
        internal readonly int Attempts,LastWin32;
        internal readonly Phase LastPhase;
        internal Result(Outcome state,int attempts,int code,Phase phase)
        { State=state;Attempts=attempts;LastWin32=code;LastPhase=phase; }
        internal bool Published { get { return State==Outcome.Published; } }
    }
    // The production implementation and Windows tests use this same seam.
    // Synthetic tests inject failures without touching installed state.
    internal interface ITemporaryStatus
    {
        Phase CurrentPhase { get; }
        void Prepare();
        void Replace();
        void Cleanup();
    }
    internal static bool IsSharingConflict(IOException error)
    { return error.HResult==unchecked((int)0x80070020)||error.HResult==unchecked((int)0x80070021); }
    internal static long MonotonicMilliseconds()
    { long ticks=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency;return (ticks/frequency)*1000+(ticks%frequency)*1000/frequency; }
    internal static Result Publish(ITemporaryStatus file,Func<bool> cancelled,Func<long> clock,Action<int> wait)
    {
        if(file==null||cancelled==null||clock==null||wait==null)throw new ArgumentNullException("Status publication dependencies");
        Result result=null;Exception failure=null;IOException lastSharing=null;
        int attempts=0;Phase lastPhase=Phase.None;
        try
        {
            if(cancelled())result=new Result(Outcome.Cancelled,0,0,Phase.None);
            else
            {
                file.Prepare();
                long started=clock(),lastTime=started;
                while(result==null)
                {
                    if(cancelled()){result=new Result(Outcome.Cancelled,attempts,lastSharing==null?0:lastSharing.HResult&65535,lastPhase);break;}
                    if(attempts!=0)
                    {
                        // A delayed scheduler wake cannot admit another native
                        // call beyond the retry window.
                        long admission=clock();
                        if(admission<lastTime)throw new InvalidOperationException("Status retry clock moved backward.");
                        lastTime=admission;
                        if(admission-started>RetryWindowMilliseconds)
                        { result=new Result(Outcome.Deferred,attempts,lastSharing.HResult&65535,lastPhase);break; }
                    }
                    attempts++;
                    try
                    {
                        file.Replace();
                        result=new Result(Outcome.Published,attempts,lastSharing==null?0:lastSharing.HResult&65535,Phase.Complete);
                    }
                    catch(IOException error)
                    {
                        // Only replacement sharing/lock conflicts are transient.
                        // ACL/path/write/flush failures remain fatal.
                        if(file.CurrentPhase!=Phase.Replace||!IsSharingConflict(error))throw;
                        lastSharing=error;lastPhase=Phase.Replace;
                        long now=clock();
                        if(now<lastTime)throw new InvalidOperationException("Status retry clock moved backward.");
                        lastTime=now;
                        long elapsed=now-started;
                        if(attempts>=MaximumAttempts||elapsed>=RetryWindowMilliseconds)
                        { result=new Result(Outcome.Deferred,attempts,error.HResult&65535,lastPhase);break; }
                        wait((int)Math.Min(RetryIntervalMilliseconds,RetryWindowMilliseconds-elapsed));
                    }
                }
            }
        }
        catch(Exception error)
        {
            error.Data["InputLift.StatusPublicationPhase"]=file.CurrentPhase.ToString();
            error.Data["InputLift.StatusPublicationHResult"]=error.HResult;
            failure=error;
        }
        try{file.Cleanup();}
        catch(Exception cleanup)
        {
            cleanup.Data["InputLift.StatusPublicationPhase"]=Phase.Cleanup.ToString();
            cleanup.Data["InputLift.StatusPublicationHResult"]=cleanup.HResult;
            Exception original=failure??lastSharing;
            if(original!=null)
            {
                var combined=new AggregateException("Status publication and owned temporary cleanup failed.",original,cleanup);
                combined.Data["InputLift.StatusPublicationPhase"]=Phase.Cleanup.ToString();
                combined.Data["InputLift.StatusPublicationHResult"]=cleanup.HResult;
                throw combined;
            }
            throw;
        }
        if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
    internal static Result Publish(ITemporaryStatus file,Func<bool> cancelled)
    { return Publish(file,cancelled,MonotonicMilliseconds,Thread.Sleep); }
    internal static void ApplyPublishedSequence(ref long sequence,long candidate,Result result)
    {
        if(result==null||candidate!=checked(sequence+1))throw new InvalidOperationException("Status publication sequence is not the next owned sequence.");
        if(result.Published)sequence=candidate;
    }

    internal sealed class NativeTemporaryStatus : ITemporaryStatus
    {
        private readonly string target,temporary,directory,owner;
        private readonly byte[] bytes;
        private readonly FileSecurity security;
        private readonly Action validateTarget;
        private bool prepared,owned;
        private Phase phase;
        internal NativeTemporaryStatus(string target,string text,FileSecurity security,Action validateTarget)
        {
            if(target==null||text==null||security==null||validateTarget==null)throw new ArgumentNullException("Status file dependencies");
            this.target=Path.GetFullPath(target);directory=Path.GetDirectoryName(this.target);
            this.security=security;this.validateTarget=validateTarget;
            owner=((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))).Value;
            bytes=new UTF8Encoding(false).GetBytes(text);
            if(bytes.Length>1048576)throw new InvalidOperationException("Ephemeral status exceeds its one-megabyte bound.");
            temporary=this.target+"."+Guid.NewGuid().ToString("N")+".tmp";
        }
        public Phase CurrentPhase { get { return phase; } }
        internal string TemporaryPath { get { return temporary; } }
        public void Prepare()
        {
            if(prepared)throw new InvalidOperationException("One status publication may create only one temporary file.");
            prepared=true;phase=Phase.Validate;validateTarget();
            phase=Phase.CreateTemporary;
            using(FileStream file=new FileStream(temporary,FileMode.CreateNew,FileSystemRights.Write,FileShare.None,4096,FileOptions.None,security))
            {
                owned=true;phase=Phase.WriteTemporary;file.Write(bytes,0,bytes.Length);
                phase=Phase.FlushTemporary;file.Flush(false);
                phase=Phase.CloseTemporary;
            }
        }
        public void Replace()
        {
            if(!owned)throw new InvalidOperationException("The current status temporary file is not owned.");
            phase=Phase.Validate;validateTarget();
            phase=Phase.Replace;
            if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
            owned=false;phase=Phase.Complete;
        }
        public void Cleanup()
        {
            if(!owned)return;
            phase=Phase.Cleanup;
            if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporary)),directory,StringComparison.OrdinalIgnoreCase)||
                !Path.GetFileName(temporary).StartsWith(Path.GetFileName(target)+".",StringComparison.Ordinal)||
                (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Owned status temporary path changed; it was not removed.");
            if(File.Exists(temporary))
            {
                if((File.GetAttributes(temporary)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidOperationException("Owned status temporary became a reparse point; it was not removed.");
                string currentOwner=((SecurityIdentifier)File.GetAccessControl(temporary).GetOwner(typeof(SecurityIdentifier))).Value;
                if(currentOwner!=owner)throw new InvalidOperationException("Owned status temporary owner changed; it was not removed.");
                File.Delete(temporary);
            }
            owned=false;
        }
    }
}
