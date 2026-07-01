using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Stamps every telemetry item with a stable <c>cloud_RoleName</c> so dashboards
/// and alerts can filter the Job Runner deterministically (instead of relying on
/// the machine/host name). Instance-level breakdowns still use cloud_RoleInstance.
/// </summary>
public sealed class RoleNameInitializer : ITelemetryInitializer
{
    public const string RoleName = "ReplicationDemo.Worker";

    public void Initialize(ITelemetry telemetry)
    {
        if (string.IsNullOrEmpty(telemetry.Context.Cloud.RoleName))
            telemetry.Context.Cloud.RoleName = RoleName;
    }
}
