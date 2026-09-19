using System.ServiceProcess;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Service;

/// <summary>
/// Entry point of the Windows Service host.
/// </summary>
/// <remarks>
/// The process only runs when the Service Control Manager starts it. Registration,
/// including the Automatic startup type (FR-01), is the installer's responsibility
/// (architecture.md §13) and is not part of this project.
/// </remarks>
internal static class Program
{
    private static void Main()
    {
        var paths = AppPaths.Default;

        ServiceBase.Run(new BootWakeMailerService(paths, new SmtpMailSender()));
    }
}
