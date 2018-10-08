namespace Unosquare.FFME.Workers
{
    using System;

    internal sealed class ReadingWorker : MediaWorker
    {
        public ReadingWorker(MediaEngine mediaCore)
            : base(nameof(RenderingWorker), TimeSpan.FromMilliseconds(10), mediaCore)
        {
            // Packet Buffer Notification Callbacks
            Container.Components.OnPacketQueueChanged = (op, packet, mediaType, state) =>
            {
                State.UpdateBufferingStats(state.Length, state.Count, state.CountThreshold);
            };
        }

        protected override void ExecuteWorkerCycle(out bool cycle)
        {
            cycle = false;

            if (IsInterruptRequested)
                return;

            if (MediaCore.ShouldReadMorePackets == false)
                return;

            Container.Read();
            cycle = true;
        }
    }
}
