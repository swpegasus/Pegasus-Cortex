namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal sealed class FixtureAssertionException(string message) : Exception(message);

internal static class FixtureAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new FixtureAssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new FixtureAssertionException(message);
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new FixtureAssertionException(message);
    }

    public static void ConfigurationFailure(
        string expectedCode,
        Action action,
        string message)
    {
        try
        {
            action();
        }
        catch (FixtureConfigurationException exception)
        {
            Equal(expectedCode, exception.Code, message);
            return;
        }

        throw new FixtureAssertionException(message);
    }
}
