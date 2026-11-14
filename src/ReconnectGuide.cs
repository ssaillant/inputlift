// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

// Pure reconnect instructions from observed Windows connection state.
internal sealed class ReconnectGuide
{
    private long offSince=-1,onSince=-1;
    private bool confirmedOff;
    internal void Reset() { offSince=onSince=-1;confirmedOff=false; }
    internal string Observe(bool connected,long milliseconds,bool bound)
    {
        if(bound) { Reset();return null; }
        if(!connected)
        {
            onSince=-1;
            if(offSince<0) offSince=milliseconds;
            if(milliseconds-offSince>=1000) confirmedOff=true;
            return confirmedOff ? "TurnKeyboardOn" : "WaitingForDisconnect";
        }
        offSince=-1;
        if(!confirmedOff) return "WaitingForKeyboard";
        if(onSince<0) onSince=milliseconds;
        if(milliseconds-onSince<15000) return "WaitingForIdentification";
        Reset();return "WaitingForKeyboard";
    }
    internal static string[] RunSelfTests(out int count)
    {
        List<string> errors=new List<string>();int cases=0;
        Action<bool,string> check=delegate(bool yes,string name) { cases++;if(!yes)errors.Add(name); };
        ReconnectGuide guide=new ReconnectGuide();
        check(guide.Observe(true,0,false)=="WaitingForKeyboard","Already connected asks for a fresh OFF cycle");
        check(guide.Observe(false,100,false)=="WaitingForDisconnect","A single false sample does not prematurely ask ON");
        check(guide.Observe(false,1099,false)=="WaitingForDisconnect","Stable interval boundary excludes999ms");
        check(guide.Observe(false,1100,false)=="TurnKeyboardOn","One-second stable false asks ON");
        check(guide.Observe(true,1200,false)=="WaitingForIdentification","Reconnected Windows state asks Shift instead of immediate second power cycle");
        check(guide.Observe(true,16199,false)=="WaitingForIdentification","Identification grace includes14999ms");
        check(guide.Observe(true,16200,false)=="WaitingForKeyboard","Missed connection eventually requests a new cycle without global failure");
        guide.Observe(false,17000,false);guide.Observe(true,17500,false);
        check(guide.Observe(true,17600,false)=="WaitingForKeyboard","A short false glitch is not a complete off cycle");
        guide.Observe(false,18000,false);guide.Observe(false,19000,false);
        check(guide.Observe(true,19100,true)==null,"Actual source binding clears reconnect instructions");
        check(guide.Observe(true,19200,false)=="WaitingForKeyboard","A later source gap needs a new cycle");
        count=cases;return errors.ToArray();
    }
}
