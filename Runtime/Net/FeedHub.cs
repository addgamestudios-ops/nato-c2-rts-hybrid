// =====================================================================
//  NATO C2 RTS Hybrid — FeedHub.cs
//  ---------------------------------------------------------------------
//  Central pub/sub for tactical data feeds. Adapters (MQTT, WebSocket,
//  CoT XML, STANAG 4609, in-process simulation) publish into the hub;
//  consumers (HUD panels, AI, persistence) subscribe by feed type.
//
//  Why a hub rather than direct adapter→consumer wiring:
//      • Decouples production (where data comes from) from consumption.
//      • Lets multiple consumers share one feed cheaply.
//      • Lets the simulation pretend to be a real feed for testing,
//        rehearsal, and red-team injects.
//
//  Threading: PublishX methods are safe to call from any thread; events
//  fire on Unity's main thread next Update(). This is critical for
//  network adapters that run on .NET worker threads.
// =====================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace NATO.C2.Net
{
    [DefaultExecutionOrder(-200)]
    [AddComponentMenu("NATO C2/Feed Hub")]
    public class FeedHub : MonoBehaviour
    {
        public static FeedHub Instance { get; private set; }

        public event Action<BftPosition>  OnBft;
        public event Action<RadarTrack>   OnRadar;
        public event Action<RadioMessage> OnRadio;
        public event Action<VideoFrame>   OnVideo;
        public event Action<CotEvent>     OnCot;

        private readonly ConcurrentQueue<BftPosition>  _bftQ   = new ConcurrentQueue<BftPosition>();
        private readonly ConcurrentQueue<RadarTrack>   _radarQ = new ConcurrentQueue<RadarTrack>();
        private readonly ConcurrentQueue<RadioMessage> _radioQ = new ConcurrentQueue<RadioMessage>();
        private readonly ConcurrentQueue<VideoFrame>   _videoQ = new ConcurrentQueue<VideoFrame>();
        private readonly ConcurrentQueue<CotEvent>     _cotQ   = new ConcurrentQueue<CotEvent>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy() { if (Instance == this) Instance = null; }

        // =================================================================
        //  Thread-safe publishers (any thread).
        // =================================================================
        public void PublishBft(BftPosition msg)    => _bftQ.Enqueue(msg);
        public void PublishRadar(RadarTrack msg)   => _radarQ.Enqueue(msg);
        public void PublishRadio(RadioMessage msg) => _radioQ.Enqueue(msg);
        public void PublishVideo(VideoFrame msg)   => _videoQ.Enqueue(msg);
        public void PublishCot(CotEvent msg)       => _cotQ.Enqueue(msg);

        // =================================================================
        //  Main-thread fan-out (Update tick).
        // =================================================================
        private void Update()
        {
            while (_bftQ.TryDequeue(out var m))   OnBft?.Invoke(m);
            while (_radarQ.TryDequeue(out var m)) OnRadar?.Invoke(m);
            while (_radioQ.TryDequeue(out var m)) OnRadio?.Invoke(m);
            while (_videoQ.TryDequeue(out var m)) OnVideo?.Invoke(m);
            while (_cotQ.TryDequeue(out var m))   OnCot?.Invoke(m);
        }
    }

    // =====================================================================
    //  Adapter interfaces — every production data source implements one.
    //  Methods are named Open/Close (not Start/Stop) to avoid colliding
    //  with the MonoBehaviour.Start() magic method on adapter classes.
    // =====================================================================
    public interface IBftAdapter
    {
        void Open(FeedHub hub);
        void Close();
    }
    public interface IRadarAdapter
    {
        void Open(FeedHub hub);
        void Close();
    }
    public interface IRadioAdapter
    {
        void Open(FeedHub hub);
        void Close();
        void Send(RadioMessage msg); // outbound radio (operator transmits)
    }
    public interface IVideoAdapter
    {
        void Open(FeedHub hub);
        void Close();
    }
    public interface ICotAdapter
    {
        void Open(FeedHub hub);
        void Close();
        void Send(CotEvent evt);
    }
}
