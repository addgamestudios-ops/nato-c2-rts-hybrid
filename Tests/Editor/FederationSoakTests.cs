// =====================================================================
//  NATO C2 RTS Hybrid — FederationSoakTests.cs
//  ---------------------------------------------------------------------
//  MOVED to Tests/PlayMode/FederationSoakTests.cs.
//
//  The full Link 16 + STANAG 5066 stack depends on
//  Link16TdmaSimulator.Update() and Stanag5066ArqRetry's wall-clock
//  retry windows. Both need PlayMode's frame-cadence Update; EditMode
//  coroutines don't drive them.
// =====================================================================
