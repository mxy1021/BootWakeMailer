# BootWakeMailer — MVP Requirements

## 1. Purpose

BootWakeMailer is a Windows 11 x64 background service that sends an email notification when the service starts or when Windows resumes from sleep/hibernation.

This document defines the MVP only. The implementation must stay small, direct, and maintainable.

## 2. Supported platform and fixed technology

- Operating system: Windows 11 x64 only.
- Language: C#.
- Runtime: .NET 10.
- Runtime identifier: win-x64.
- Windows background service: ServiceBase.
- Local configuration UI: WinForms.
- SMTP client: MailKit.
- Final deployment: self-contained publish.
- Configuration path: `%ProgramData%\BootWakeMailer\config.json`.
- Queue path: `%ProgramData%\BootWakeMailer\queue.json`.
- Status path: `%ProgramData%\BootWakeMailer\status.json`.
- SMTP password protection: Windows DPAPI.
- Installation entry points: `install.cmd` and `install.ps1`.
- Uninstallation entry points: `uninstall.cmd` and `uninstall.ps1`.

## 3. Functional requirements

### FR-01 Windows service startup
- The application shall install as a Windows Service.
- The service startup type shall be Automatic.
- Every service start shall be treated as one Startup event.
- The service shall send one notification email for each Startup event.
- The service shall not attempt to distinguish a real Windows boot from a manual/service-manager restart.

### FR-02 Resume notification

- The service shall listen for Windows power-resume notifications.
- Only `PowerBroadcastStatus.ResumeAutomatic` shall create a resume notification.
- `ResumeSuspend` shall not be handled.
- One `ResumeAutomatic` event shall create one email task.

### FR-03 SMTP configuration

- The MVP shall support exactly one SMTP configuration.
- The MVP shall support exactly one sender account/address.
- The MVP shall support exactly one recipient address.
- SMTP settings shall be stored in `config.json`.
- The SMTP password shall never be stored as plaintext; it shall be protected with Windows DPAPI.
- The configuration shall contain only the fields required to connect, authenticate, identify the sender, and identify the recipient.

### FR-04 Email content

- Notification email content shall be fixed by the application; there is no template system.
- The notification shall identify the event type: Startup or ResumeAutomatic.
- The notification shall include the event occurrence time and local computer name.

### FR-05 Local durable queue

- Before attempting a notification send, the service shall create a local queue task in `queue.json`.
- A failed SMTP send shall remain in the queue.
- A queued task shall remain pending until the SMTP server accepts the message.
- A successfully accepted task shall be removed from the pending queue.
- The queue shall survive service restarts and Windows restarts.
- The MVP shall use JSON file storage only; no database is permitted.

### FR-06 Automatic retry

- The service shall automatically retry pending tasks every 60 seconds.
- Each retry cycle shall attempt pending tasks in FIFO order.
- Failed tasks shall remain pending for a later retry cycle.
- Retries shall continue without a fixed maximum attempt count.
- A successful task shall not be retried again.

### FR-07 SMTP success criterion

- A send is successful when the SMTP server has accepted the message and MailKit completes the send operation successfully.
- BootWakeMailer shall not verify final inbox delivery.
- Delivery delays, spam filtering, forwarding, mailbox rejection after SMTP acceptance, and read receipts are outside the success criterion.

### FR-08 WinForms configuration tool

The local WinForms tool shall provide only these MVP functions:
- Edit the single SMTP configuration.
- Save the configuration.
- Send one test email using the current configuration.
- Display the current Windows Service running state.
- Display the current number of pending queue tasks.
- Display the most recent successful notification send time.
- Display the most recent service/send error.
- Request an immediate retry of pending notification tasks.

The configuration tool is local-only and shall not host a Web API or Web UI.

### FR-09 Installation

- `install.cmd` shall provide the one-click installation entry point and invoke `install.ps1`.
- Installation shall run with the administrator privileges required to register a Windows Service.
- Installation shall place the self-contained win-x64 application files under `%ProgramFiles%\BootWakeMailer`.
- Installation shall create `%ProgramData%\BootWakeMailer` when it does not exist.
- Installation shall register the BootWakeMailer Windows Service with Automatic startup.
- Installation shall start the service after registration.

### FR-10 Uninstallation

- `uninstall.cmd` shall provide the one-click uninstallation entry point and invoke `uninstall.ps1`.
- Uninstallation shall run with the administrator privileges required to remove a Windows Service.
- Uninstallation shall stop the service if it is running.
- Uninstallation shall remove the Windows Service registration.
- Uninstallation shall remove `%ProgramFiles%\BootWakeMailer`.
- Uninstallation shall remove `%ProgramData%\BootWakeMailer`.

## 4. Reliability and error requirements

- Missing or invalid configuration shall not crash the service process.
- File I/O, DPAPI, SMTP, and power-event handling failures shall be caught and recorded as the latest error.
- A notification task shall not be silently lost because SMTP is unavailable.

## 5. Data and concurrency requirements

- JSON files shall be UTF-8.
- Writes to `config.json`, `queue.json`, and `status.json` shall be atomic so an interrupted write does not leave a partially written JSON file.
- The Windows Service shall be the only process that modifies `queue.json` and `status.json`.
- The WinForms tool may read `queue.json` and `status.json`.
- The WinForms tool shall modify only `config.json`; immediate retry shall be requested through the Windows Service rather than by editing the queue file.
- Timestamps persisted in JSON shall use UTC in ISO 8601 format.

## 6. Simplicity and maintainability constraints

- Prefer a small solution with a minimal number of projects and dependencies.
- Do not introduce infrastructure that is unnecessary for this MVP.
- Do not introduce a general-purpose event bus, repository abstraction, plugin system, template engine, or remote-control protocol.
- Shared code is allowed only where it avoids direct duplication between the service and WinForms tool.
- The implementation shall remain understandable without a complex layered architecture.

## 7. Explicitly out of MVP scope

The following are not part of this MVP and shall not be implemented:
- Database or SQLite.
- Web API or Web UI.
- Multiple SMTP servers.
- Multiple sender accounts.
- Multiple recipients.
- Email template system.
- Automatic update.
- System tray application.
- Cloud service dependency.
- RabbitMQ.
- Redis.
- Docker.
- Complex layered architecture.
- Final-delivery/inbox confirmation.
- Handling `ResumeSuspend`.

## 8. MVP acceptance criteria

The MVP is accepted when all of the following are true:

1. The self-contained win-x64 build installs on Windows 11 x64 through `install.cmd`.
2. The installed Windows Service is configured for Automatic startup.
3. Starting or restarting the service creates one Startup email task and attempts to send it.
4. A `ResumeAutomatic` power event creates one resume email task and attempts to send it.
5. `ResumeSuspend` does not create a task.
6. With working SMTP configuration, MailKit completes the send after the SMTP server accepts the message.
7. With SMTP/network failure, the task remains in `queue.json`.
8. Pending tasks are retried every 60 seconds and removed only after successful SMTP acceptance.
9. Pending tasks survive service and Windows restarts.
10. The WinForms tool can edit and save the one SMTP/sender/recipient configuration.
11. The stored SMTP password is DPAPI-protected rather than plaintext.
12. The WinForms tool can send a test email and show its result.
13. The WinForms tool can display service state, pending count, latest successful send time, and latest error.
14. The WinForms tool can request an immediate retry of pending tasks.
15. `uninstall.cmd` removes the service, installed binaries, and BootWakeMailer application data.
16. No excluded MVP functionality or infrastructure is introduced.

## 9. Development boundary for the current phase

This phase produces only `requirements.md` and `architecture.md`.
No application, service, WinForms, installer, test, or business-code implementation is part of this phase.
