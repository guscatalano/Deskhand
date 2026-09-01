namespace Deskhand.Core.Services;

public record EnvActionDto(bool Ok, string Name, string? Value, string Scope, string? Error = null);

/// <summary>Read and set environment variables at process, user, or machine scope. Machine scope requires
/// elevation. Setting user/machine variables is persistent (they land in the registry and are broadcast to new
/// processes); the running Deskhand process does not see them until restarted.</summary>
public static class EnvironmentService
{
    public static EnvActionDto Get(string name, string? scope)
    {
        var target = Target(scope);
        try { return new EnvActionDto(true, name, Environment.GetEnvironmentVariable(name, target), target.ToString()); }
        catch (Exception ex) { return new EnvActionDto(false, name, null, target.ToString(), ex.Message); }
    }

    public static EnvActionDto Set(string name, string? value, string? scope)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new EnvActionDto(false, name, null, scope ?? "process", "No variable name.");
        var target = Target(scope);
        try
        {
            Environment.SetEnvironmentVariable(name, value, target);   // value=null deletes it
            return new EnvActionDto(true, name, value, target.ToString());
        }
        catch (System.Security.SecurityException)
        {
            return new EnvActionDto(false, name, value, target.ToString(),
                target == EnvironmentVariableTarget.Machine ? "Access denied — machine scope requires running elevated." : "Access denied.");
        }
        catch (Exception ex) { return new EnvActionDto(false, name, value, target.ToString(), ex.Message); }
    }

    private static EnvironmentVariableTarget Target(string? scope) => (scope ?? "process").Trim().ToLowerInvariant() switch
    {
        "user" => EnvironmentVariableTarget.User,
        "machine" or "system" => EnvironmentVariableTarget.Machine,
        _ => EnvironmentVariableTarget.Process,
    };
}
