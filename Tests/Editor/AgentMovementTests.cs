// =====================================================================
//  NATO C2 RTS Hybrid — AgentMovementTests.cs
//  ---------------------------------------------------------------------
//  MOVED to Tests/PlayMode/AgentMovementTests.cs.
//
//  This test exercises ORCA (Burst jobs) + the NATO_C2_Manager Update
//  loop, both of which only run correctly under PlayMode's full
//  lifecycle. EditMode coroutines don't tick MonoBehaviour.Update and
//  the JobSystem schedules/completes differently, so the test couldn't
//  reliably pass in EditMode.
// =====================================================================
