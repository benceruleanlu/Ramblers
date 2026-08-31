namespace Ramblers;

internal static class TurnReferenceRetentionPolicy
{
    internal static bool ShouldRetain(bool responseHadFunctionCallBatch)
    {
        return responseHadFunctionCallBatch;
    }
}

internal static class PresentationRetentionPolicy
{
    internal static bool MustEndBatchBeforeNextCall(
        bool retainUntilAssistantAudio,
        bool hasUndispatchedCalls)
    {
        return retainUntilAssistantAudio && hasUndispatchedCalls;
    }
}
