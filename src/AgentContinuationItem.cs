namespace Ramblers;

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
