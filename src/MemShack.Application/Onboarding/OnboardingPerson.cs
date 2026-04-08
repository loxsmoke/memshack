namespace MemShack.Application.Onboarding;

public sealed record OnboardingPerson(
    string Name,
    string Relationship = "",
    string Context = "personal");
