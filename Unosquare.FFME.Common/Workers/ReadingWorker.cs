namespace Unosquare.FFME.Workers
{
    internal sealed class ReadingWorker : MediaWorker
    {
        public ReadingWorker(MediaEngine mediaCore)
            : base(nameof(RenderingWorker), mediaCore)
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
