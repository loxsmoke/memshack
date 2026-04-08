namespace MemShack.Application.Onboarding;

public sealed record OnboardingSetup(
    string Mode,
    IReadOnlyList<OnboardingPerson> People,
    IReadOnlyList<string> Projects,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> Wings);
