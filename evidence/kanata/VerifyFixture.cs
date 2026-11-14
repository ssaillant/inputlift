// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Pure fixture verification against the public core API. No native calls or networking.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using InputLift.Core;

internal sealed class FixtureState
{
    public bool connectionBound { get; set; }
    public bool bound { get; set; }
    public bool pressed { get; set; }
}
internal sealed class FixtureStep
{
    public string id { get; set; }
    public string operation { get; set; }
    public string kind { get; set; }
    public long timestamp { get; set; }
    public string packetHex { get; set; }
    public string[] expectedTransitions { get; set; }
    public string[] expectedReasons { get; set; }
    public FixtureState expectedState { get; set; }
}
internal sealed class FixtureScenario
{
    public string id { get; set; }
    public FixtureStep[] steps { get; set; }
}
internal sealed class FixtureDocument
{
    public int schemaVersion { get; set; }
    public bool synthetic { get; set; }
    public string selector { get; set; }
    public Dictionary<string, int> packetKinds { get; set; }
    public int[] ordinaryReportIndicesNeverRead { get; set; }
    public FixtureStep[] setup { get; set; }
    public FixtureScenario[] scenarios { get; set; }
}
internal static class VerifyFixture
{
    private static int steps, packets, resets, transitions, events, guardedReads;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 16 };

    private static void Require(bool okay, string message)
    { if (!okay) throw new InvalidOperationException(message); }

    private static BluetoothPacketKind PacketKind(string name)
    {
        switch (name)
        {
            case "HciEvent": return BluetoothPacketKind.HciEvent;
            case "ReceivedAcl": return BluetoothPacketKind.ReceivedAcl;
            case "SentAcl": return BluetoothPacketKind.SentAcl;
            default: throw new InvalidDataException("Unsupported fixture packet classification.");
        }
    }

    private static byte[] Packet(string text)
    {
        Require(text != null && text.Length <= 1024, "Fixture packet is absent or oversized.");
        string[] words = text.Split(' ');
        Require(words.Length >= 1 && words.Length <= 128, "Fixture packet byte count is outside its bound.");
        byte[] packet = new byte[words.Length];
        for (int i = 0; i < words.Length; i++)
            Require(words[i].Length == 2 && byte.TryParse(words[i], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out packet[i]), "Fixture contains a non-byte hex token.");
        return packet;
    }

    private static void CheckStep(FnDecoder decoder, FnInputAdapter adapter, FixtureStep step, int[] forbidden)
    {
        Require(step != null && !String.IsNullOrEmpty(step.id), "Fixture step lacks an identifier.");
        Require(step.expectedTransitions != null && step.expectedReasons != null && step.expectedState != null,
            "Fixture step lacks explicit expectations.");
        Require(step.expectedTransitions.Length == step.expectedReasons.Length && step.expectedTransitions.Length <= 3,
            "Fixture transition expectations are invalid.");
        IList<FnTransition> actual;
        if (step.operation == "reset")
        {
            Require(step.packetHex == null && step.kind == null, "A reset cannot include packet data.");
            actual = decoder.Reset(step.timestamp); resets++;
        }
        else
        {
            Require(step.operation == "packet", "Unsupported fixture operation.");
            byte[] packet = Packet(step.packetHex);
            bool guarded = packet.Length == 19;
            if (guarded)
                foreach (int index in forbidden)
                    Require(index >= 0 && index < packet.Length && packet[index] == 0,
                        "Ordinary report slots must contain only synthetic zero placeholders.");
            actual = decoder.Process(PacketKind(step.kind), packet.Length, delegate(int index)
            {
                Require(index >= 0 && index < packet.Length, "Public decoder read outside the supplied packet.");
                if (guarded)
                    foreach (int ordinaryIndex in forbidden)
                        Require(index != ordinaryIndex, "Public decoder read an ordinary key slot.");
                guardedReads++;
                return packet[index];
            }, step.timestamp);
            packets++;
        }
        Require(actual.Count == step.expectedTransitions.Length, step.id + ": wrong transition count.");
        IList<InputEvent> generic = adapter.Convert(actual);
        Require(generic.Count == actual.Count, step.id + ": event adapter changed transition count.");
        for (int i = 0; i < actual.Count; i++)
        {
            Require(actual[i].Kind.ToString() == step.expectedTransitions[i] && actual[i].Timestamp == step.timestamp &&
                actual[i].Reason == step.expectedReasons[i], step.id + ": wrong transition kind, timestamp or reason.");
            Require(generic[i].Kind.ToString() == step.expectedTransitions[i] && generic[i].Timestamp == step.timestamp &&
                generic[i].SourceId == "synthetic-keyboard" &&
                generic[i].ControlId == (actual[i].Kind == FnTransitionKind.Reset ? null : "fn"),
                step.id + ": wrong generic event metadata.");
        }
        Require(decoder.IsConnectionBound == step.expectedState.connectionBound && decoder.IsBound == step.expectedState.bound &&
            decoder.IsPressed == step.expectedState.pressed, step.id + ": wrong public decoder state.");
        transitions += actual.Count; events += generic.Count; steps++;
    }

    private static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 1, "Supply the one synthetic fixture JSON path.");
            FileInfo info = new FileInfo(args[0]);
            Require(info.Length > 0 && info.Length <= 65536, "Fixture file is empty or oversized.");
            byte[] contents = File.ReadAllBytes(args[0]);
            FixtureDocument fixture = Json.Deserialize<FixtureDocument>(System.Text.Encoding.UTF8.GetString(contents));
            Require(fixture != null && fixture.schemaVersion == 1 && fixture.synthetic && fixture.selector == "102030405060",
                "Only the reviewed schema-1 synthetic selector fixture is accepted.");
            Require(fixture.packetKinds != null && fixture.packetKinds.Count == 3 && fixture.packetKinds["HciEvent"] == 2 &&
                fixture.packetKinds["ReceivedAcl"] == 3 && fixture.packetKinds["SentAcl"] == 4,
                "BTHPORT classifications must be explicit; these are not H4 bytes.");
            Require(fixture.ordinaryReportIndicesNeverRead != null && fixture.ordinaryReportIndicesNeverRead.Length == 7,
                "Fixture must state its ordinary-key reader guard.");
            int[] expectedForbidden = { 10, 12, 13, 14, 15, 16, 17 };
            for (int i = 0; i < expectedForbidden.Length; i++)
                Require(fixture.ordinaryReportIndicesNeverRead[i] == expectedForbidden[i], "Fixture ordinary-key guard changed.");
            Require(fixture.setup != null && fixture.setup.Length == 4 && fixture.scenarios != null &&
                fixture.scenarios.Length >= 1 && fixture.scenarios.Length <= 16, "Fixture setup/scenario bounds are invalid.");
            foreach (FixtureScenario scenario in fixture.scenarios)
            {
                Require(scenario != null && !String.IsNullOrEmpty(scenario.id) && scenario.steps != null &&
                    scenario.steps.Length >= 1 && scenario.steps.Length <= 64, "Fixture scenario is incomplete or oversized.");
                FnDecoder decoder = new FnDecoder(fixture.selector);
                FnInputAdapter adapter = new FnInputAdapter("synthetic-keyboard", "fn");
                foreach (FixtureStep step in fixture.setup) CheckStep(decoder, adapter, step, expectedForbidden);
                foreach (FixtureStep step in scenario.steps) CheckStep(decoder, adapter, step, expectedForbidden);
            }
            string digest;
            using (SHA256 sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(contents)).Replace("-", "").ToLowerInvariant();
            Console.WriteLine(Json.Serialize(new { status = "ok", fixtureSha256 = digest, scenarios = fixture.scenarios.Length,
                checkedSteps = steps, packetRecords = packets, explicitResets = resets, decodedTransitions = transitions,
                genericEvents = events, boundedReaderCalls = guardedReads, ordinaryKeyReads = 0,
                hardwareValidation = false, liveTransportTested = false, nativeActionsPerformed = false }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Synthetic fixture verification failed: " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
}
