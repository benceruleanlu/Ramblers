namespace Ramblers;

/// <summary>
/// An extra conversation item a job contributes alongside its tool output: a
/// text report, an image, or both. Jobs build these themselves so a new action
/// can report what it observed without the transport learning a new type.
/// Image bytes stay bytes, with their media type, until the transport encodes
/// them.
/// </summary>
internal sealed class AgentContinuationItem
{
    internal string Text;
    internal byte[] ImageBytes;
    internal string ImageMediaType;

    internal static AgentContinuationItem FromText(string text)
    {
        return new AgentContinuationItem { Text = text };
    }

    internal static AgentContinuationItem FromImage(
        string text,
        byte[] imageBytes,
        string imageMediaType)
    {
        return new AgentContinuationItem
        {
            Text = text,
            ImageBytes = imageBytes,
            ImageMediaType = imageMediaType
        };
    }
}
