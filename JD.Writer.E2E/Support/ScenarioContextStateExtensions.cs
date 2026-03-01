using Reqnroll;

namespace JD.Writer.E2E.Support;

internal static class ScenarioContextStateExtensions
{
    public static void SetState(this ScenarioContext context, ScenarioState state)
    {
        context[nameof(ScenarioState)] = state;
    }

    public static ScenarioState GetState(this ScenarioContext context)
    {
        return (ScenarioState)context[nameof(ScenarioState)];
    }
}
