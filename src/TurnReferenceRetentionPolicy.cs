namespace Ramblers;

/// <summary>
/// Pure lifetime rule for a speech-turn reference at response completion. The
/// decision follows response protocol metadata, never Unity-frame timing.
/// </summary>
internal static class TurnReferenceRetentionPolicy
{
    internal static bool ShouldRetain(bool responseHadFunctionCallBatch)
    {
        return responseHadFunctionCallBatch;
    }
}

/// <summary>
/// A presentation completion must end its current tool sequence so the model
/// can consume the image before choosing another action and the companion can
/// retain gaze until that presentation starts.
/// </summary>
internal static class PresentationRetentionPolicy
{
    internal static bool MustEndBatchBeforeNextCall(
        bool retainUntilAssistantAudio,
        bool hasUndispatchedCalls)
    {
        return retainUntilAssistantAudio && hasUndispatchedCalls;
    }
}
