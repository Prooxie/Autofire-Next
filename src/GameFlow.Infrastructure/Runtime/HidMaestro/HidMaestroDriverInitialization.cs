using System.Reflection;

namespace GameFlow.Infrastructure.Runtime.HidMaestro;

/// <summary>
/// Installs once per SDK context, before any outputs exist. Callers serialize access.
/// A failed install requires a restart: retrying a global SDK sweep after other
/// slots have started could destroy their devices.
/// </summary>
internal sealed class HidMaestroDriverInitialization(object context)
{
    private bool attempted;
    private string? installationFailure;

    public bool EnsureInstalled(out string? failure)
    {
        if (!attempted)
        {
            attempted = true;
            try
            {
                var install = context.GetType().GetMethod("InstallDriver", BindingFlags.Public | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null)
                    ?? throw new MissingMethodException("The SDK does not expose InstallDriver().");
                install.Invoke(context, null);
            }
            catch (Exception exception)
            {
                var cause = (exception as TargetInvocationException)?.InnerException ?? exception;
                installationFailure = $"HIDMaestro driver installation failed: {cause.Message} Resolve the error and restart GameFlow.";
            }
        }

        failure = installationFailure;
        return failure is null;
    }
}
