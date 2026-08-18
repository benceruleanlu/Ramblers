namespace Ramblers;

/// <summary>
/// An extra conversation item a job contributes alongside its tool output: a
/// text report, an image, or both. Jobs build these themselves so a new action
/// can report what it observed without the transport learning a new type.
/// Image bytes stay bytes until the transport encodes them.
/// </summary>
internal sealed class AgentContinuationItem
{
    internal string Text;
    internal byte[] ImageJpeg;

    internal static AgentContinuationItem FromText(string text)
    {
        return new AgentContinuationItem { Text = text, ImageJpeg = null };
    }

    internal static AgentContinuationItem FromImage(string text, byte[] imageJpeg)
    {
        return new AgentContinuationItem { Text = text, ImageJpeg = imageJpeg };
    }
}
