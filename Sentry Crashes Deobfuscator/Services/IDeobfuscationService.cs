namespace Sentry_Crashes_Deobfuscator.Services
{

    public interface IDeobfuscationService
    {
        string DeobfuscateEnvelope(string envelopeText);

        DeobfuscationStats GetLastStats();
    }

    public class DeobfuscationStats
    {
        public int FramesDeobfuscated { get; set; }
        public int FramesSkipped { get; set; }
        public int FramesFailed { get; set; }
        public int TotalFrames => FramesDeobfuscated + FramesSkipped + FramesFailed;
        public bool HasDeobfuscations => FramesDeobfuscated > 0;
    }
}