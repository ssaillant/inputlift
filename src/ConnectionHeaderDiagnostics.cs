// Copyright (c) 2026 GlobeRelay contributors
// SPDX-License-Identifier: MIT
// Bounded protocol-header counters only. No ordinary key fields, packet copies,
// handles/addresses in output, I/O or decisions that enable relay input.
using System;
using System.Collections.Generic;

internal sealed class ConnectionHeaderDiagnostics
{
    private const int BucketLimit = 128;
    private readonly byte[] target;
    private readonly SortedDictionary<string, PacketShape> packets = new SortedDictionary<string, PacketShape>(StringComparer.Ordinal);
    private readonly SortedDictionary<string, EventShape> events = new SortedDictionary<string, EventShape>(StringComparer.Ordinal);
    private long packetBucketsDropped, eventBucketsDropped;

    public sealed class PacketShape
    {
        public int Kind, Length;
        public long Count;
    }
    public sealed class EventShape
    {
        public int Code, Length, DeclaredLength, Status;
        public string Category;
        public long Count;
    }
    public sealed class Summary
    {
        public List<PacketShape> PacketShapes;
        public List<EventShape> HciEventShapes;
        public long PacketBucketsDropped, EventBucketsDropped;
    }

    internal ConnectionHeaderDiagnostics(byte[] targetBigEndian)
    {
        if(targetBigEndian==null || targetBigEndian.Length!=6) throw new ArgumentException("Expected fixed six-byte target address.");
        target=(byte[])targetBigEndian.Clone();
    }
    internal void Observe(byte kind,int length,Func<int,byte> read)
    {
        if(length<0 || read==null) throw new ArgumentException("Invalid bounded protocol-header observation.");
        string packetKey=kind+":"+length;
        PacketShape packet;
        if(packets.TryGetValue(packetKey,out packet)) packet.Count++;
        else if(packets.Count<BucketLimit) packets.Add(packetKey,new PacketShape { Kind=kind,Length=length,Count=1 });
        else packetBucketsDropped++;
        // Kind2 is a BTHPORT HCI event, not an H4 type or ACL/HID packet.
        // All ACL reports are counted by metadata without reading ANY payload.
        if(kind!=2) return;
        int code=-1,declared=-1,status=-1;
        string category;
        if(length<2) category="short-hci-header";
        else
        {
            code=read(0); declared=read(1);
            if(length!=declared+2) category="hci-length-mismatch";
            else if(code!=3) category=code==5 ? "disconnect-event" : "other-hci-event";
            else if(length!=13) category="connection-complete-shape";
            else
            {
                // The exact Connection Complete frame contains protocol fields,
                // not HID key slots or encryption-key payloads. Do not inspect
                // any other HCI event beyond its two-byte code/length header.
                status=read(2);
                if(status!=0) category="connection-status-error";
                else if(read(11)!=1) category="connection-not-acl";
                else if((read(3)|(read(4)<<8))>0x0EFF) category="connection-handle-range";
                else
                {
                    bool match=true;
                    for(int i=0;i<6;i++) if(read(5+i)!=target[5-i]) match=false;
                    category=match ? "connection-matches-target-policy" : "connection-other-address";
                }
            }
        }
        string eventKey=code+":"+length+":"+declared+":"+status+":"+category;
        EventShape shape;
        if(events.TryGetValue(eventKey,out shape)) shape.Count++;
        else if(events.Count<BucketLimit) events.Add(eventKey,new EventShape { Code=code,Length=length,
            DeclaredLength=declared,Status=status,Category=category,Count=1 });
        else eventBucketsDropped++;
    }
    internal Summary Snapshot()
    {
        Summary result=new Summary { PacketShapes=new List<PacketShape>(),HciEventShapes=new List<EventShape>(),
            PacketBucketsDropped=packetBucketsDropped,EventBucketsDropped=eventBucketsDropped };
        foreach(PacketShape p in packets.Values) result.PacketShapes.Add(new PacketShape { Kind=p.Kind,Length=p.Length,Count=p.Count });
        foreach(EventShape e in events.Values) result.HciEventShapes.Add(new EventShape { Code=e.Code,Length=e.Length,
            DeclaredLength=e.DeclaredLength,Status=e.Status,Category=e.Category,Count=e.Count });
        return result;
    }
    private static byte[] ConnectionFixture()
    {
        return new byte[] { 3,11,0,1,0,6,5,4,3,2,1,1,0 };
    }
    internal static string[] RunSelfTests(out int count)
    {
        int cases=0; List<string> failures=new List<string>();
        Action<bool,string> check=delegate(bool good,string name) { cases++; if(!good) failures.Add(name); };
        byte[] address={1,2,3,4,5,6};
        ConnectionHeaderDiagnostics d=new ConnectionHeaderDiagnostics(address);
        Func<int,byte> never=delegate(int n) { throw new Exception("ACL/command payload access is prohibited."); };
        foreach(byte kind in new byte[] { 0,1,3,4,255 }) d.Observe(kind,19,never);
        check(d.Snapshot().HciEventShapes.Count==0 && d.Snapshot().PacketShapes.Count==5,"ACL and unrelated kinds never read payload bytes");
        d.Observe(2,1,never);
        check(d.Snapshot().HciEventShapes[0].Category=="short-hci-header","Short HCI event never reads outside length");
        byte[] secretEvent={0x18,23};
        d.Observe(2,25,delegate(int n) { if(n>1) throw new Exception("Non-connection event payload is prohibited."); return secretEvent[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="other-hci-event" && e.Code==0x18; }),"Encryption-related HCI event observes only code and length");
        byte[] fixture=ConnectionFixture();
        d.Observe(2,13,delegate(int n) { if(n>=12) throw new Exception("Unneeded Connection Complete field read."); return fixture[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-matches-target-policy"; }),"Exact classic target connection is distinguished");
        fixture[5]=77;
        d.Observe(2,13,delegate(int n) { return fixture[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-other-address"; }),"Wrong address is categorized without outputting it");
        fixture=ConnectionFixture(); fixture[2]=4;
        d.Observe(2,13,delegate(int n) { if(n>2) throw new Exception("Failed connection needs no later payload fields."); return fixture[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-status-error" && e.Status==4; }),"Connection status error is preserved as protocol metadata");
        fixture=ConnectionFixture(); fixture[11]=0;
        d.Observe(2,13,delegate(int n) { return fixture[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-not-acl"; }),"Non-ACL Connection Complete is distinguished");
        fixture=ConnectionFixture(); fixture[4]=0x10;
        d.Observe(2,13,delegate(int n) { return fixture[n]; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-handle-range"; }),"Out-of-policy handle is distinguished without outputting handle");
        d.Observe(2,3,delegate(int n) { return n==0 ? (byte)3 : (byte)0; });
        d.Observe(2,3,delegate(int n) { return n==0 ? (byte)3 : (byte)1; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="hci-length-mismatch"; }) &&
            d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="connection-complete-shape"; }),"Framing and valid-but-unsupported connection length are separate");
        d.Observe(2,6,delegate(int n) { if(n>1) throw new Exception("Disconnect details not allowed here."); return n==0 ? (byte)5 : (byte)4; });
        check(d.Snapshot().HciEventShapes.Exists(delegate(EventShape e) { return e.Category=="disconnect-event"; }),"Disconnect header is counted even before target binding");
        for(int i=0;i<140;i++) d.Observe(3,100+i,never);
        check(d.Snapshot().PacketShapes.Count==128 && d.Snapshot().PacketBucketsDropped>0,"Packet shape memory is bounded with explicit overflow count");
        for(int i=0;i<140;i++) { int code=i+32; d.Observe(2,2,delegate(int n) { return n==0 ? (byte)code : (byte)0; }); }
        check(d.Snapshot().HciEventShapes.Count==128 && d.Snapshot().EventBucketsDropped>0,"HCI shape memory is bounded with explicit overflow count");
        Summary snapshot=d.Snapshot(); snapshot.PacketShapes.Clear();
        check(d.Snapshot().PacketShapes.Count==128,"Snapshots cannot mutate stored counters");
        count=cases; return failures.ToArray();
    }
}
