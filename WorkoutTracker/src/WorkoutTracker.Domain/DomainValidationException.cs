namespace WorkoutTracker.Domain;

public sealed class DomainValidationException(string message) : ArgumentException(message);

