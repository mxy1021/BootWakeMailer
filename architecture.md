# BootWakeMailer — MVP Architecture

## 1. Architecture goals

The architecture shall satisfy `requirements.md` with the fewest moving parts practical for a maintainable Windows 11 x64 application.

Primary rules:
- Windows Service owns event capture, durable notification queue processing, retry, and status writes.
- WinForms owns local configuration editing and test-mail UX.
- Shared code exists only to avoid duplicating file models, DPAPI, JSON persistence, and SMTP send logic.
- JSON files are the only persistence mechanism.
- There is no network-facing application API and no database.

## 2. Project structure

```text
BootWakeMailer/
├─ BootWakeMailer.sln
├─ requirements.md
├─ architecture.md
├─ install.cmd
├─ install.ps1
├─ uninstall.cmd
├─ uninstall.ps1
└─ src/
   ├─ BootWakeMailer.Shared/
   ├─ BootWakeMailer.Service/
   └─ BootWakeMailer.ConfigTool/
```

No additional application projects are required for the MVP.

## 3. Project responsibilities

### 3.1 BootWakeMailer.Shared

A small class library shared by the service and WinForms tool. It contains only:
- DTOs for `config.json`, `queue.json`, and `status.json`.
- JSON load/save helpers with atomic file replacement.
- DPAPI password protect/unprotect helpers.
- SMTP send logic based on MailKit.
- Shared constants such as file paths, service name, custom command ID, and schema version.
- Minimal validation used by both applications.

It shall not become a general domain/framework layer.

### 3.2 BootWakeMailer.Service

A `ServiceBase` Windows Service responsible for:
- Treating every service start as a Startup event.
- Enabling power-event handling.
- Handling only `PowerBroadcastStatus.ResumeAutomatic`.
- Persisting notification tasks to `queue.json`.
- Attempting queued SMTP sends.
- Running the 60-second retry timer.
- Processing the immediate-retry service command.
- Updating `status.json`.
- Serializing queue-processing operations so two retry/send cycles cannot run concurrently.

### 3.3 BootWakeMailer.ConfigTool

A local WinForms executable responsible for:
- Loading and editing the single SMTP configuration.
- Protecting the password and saving `config.json`.
- Sending a direct test email.
- Querying Windows Service state through `ServiceController`.
- Reading `queue.json` to show pending count.
- Reading `status.json` to show latest success time and latest error.
- Requesting immediate retry through a custom Windows Service command.

The WinForms tool shall not directly modify `queue.json` or `status.json`.

## 4. Fixed filesystem layout

Installed binaries:
- `%ProgramFiles%\BootWakeMailer\`

Application data:
- `%ProgramData%\BootWakeMailer\config.json`
- `%ProgramData%\BootWakeMailer\queue.json`
- `%ProgramData%\BootWakeMailer\status.json`

All persisted JSON shall be UTF-8 and use UTC ISO 8601 timestamps.

For all JSON writes:
1. Serialize the complete new document to a temporary file in the same directory.
2. Flush/close the temporary file.
3. Atomically replace or move it over the target file.

This avoids exposing readers to a partially written JSON document.

## 5. config.json structure

The MVP stores one SMTP connection, one sender, and one recipient.

```json
{
  "schemaVersion": 1,
  "smtp": {
    "host": "smtp.example.com",
    "port": 587,
    "securityMode": "StartTls",
    "username": "sender@example.com",
    "encryptedPassword": "<DPAPI-protected Base64>"
  },
  "fromAddress": "sender@example.com",
  "toAddress": "recipient@example.com"
}
```

Rules:
- `securityMode` maps directly to the limited MailKit connection mode needed by the UI: `Auto`, `StartTls`, `SslOnConnect`, or `None`.
- No SMTP array is allowed.
- No recipient array is allowed.
- No subject/body templates are stored in configuration.
- `encryptedPassword` is Base64 text containing DPAPI-protected bytes.
- DPAPI shall use machine scope so both the Windows Service account and the local WinForms process can use the same saved credential.
- On each send attempt, the service reads the current configuration; pending queue items therefore use the latest saved SMTP settings.

## 6. queue.json structure

`queue.json` contains only pending notification tasks.

```json
{
  "schemaVersion": 1,
  "items": [
    {
      "id": "f6507cf8-e800-4c53-88ef-4a96e10a65d8",
      "eventType": "Startup",
      "occurredAtUtc": "2026-09-19T06:30:00Z",
      "computerName": "ZEN-PC",
      "attemptCount": 1,
      "lastAttemptAtUtc": "2026-09-19T06:30:01Z",
      "lastError": "SMTP connection failed"
    }
  ]
}
```
Queue rules:
- `eventType` is limited to `Startup` or `ResumeAutomatic`.
- A task is written to the queue before its first SMTP attempt.
- `attemptCount` increments immediately before each SMTP attempt.
- `lastAttemptAtUtc` and `lastError` describe the most recent attempt for that task.
- Items are processed by `occurredAtUtc` in FIFO order.
- After SMTP acceptance, the item is removed and `queue.json` is atomically rewritten.
- If the queue file is missing, the service may initialize an empty queue.
- If the queue file exists but is invalid/corrupt, the service shall record an error and shall not silently replace it with an empty queue.

The Windows Service is the sole writer of this file.

## 7. status.json structure

`status.json` stores only the small amount of status that must survive process restarts.

```json
{
  "schemaVersion": 1,
  "lastSuccessfulSendAtUtc": "2026-09-19T06:30:02Z",
  "lastError": {
    "occurredAtUtc": "2026-09-19T06:29:40Z",
    "operation": "SmtpSend",
    "type": "SmtpCommandException",
    "message": "SMTP server rejected the operation"
  }
}
```

Rules:
- `lastSuccessfulSendAtUtc` changes only after a queued notification is accepted by SMTP.
- `lastError` is replaced when a newer service/config/file/SMTP processing error occurs.
- A later success does not erase `lastError`; the UI therefore shows the most recent historical error until another error replaces it.
- Pending count is not duplicated into `status.json`; the WinForms tool derives it from `queue.json`.
- Service running/stopped state is not duplicated into `status.json`; it comes from `ServiceController`.

## 8. Startup event processing flow

Every service start is intentionally an event; there is no boot-vs-restart detection.

1. `ServiceBase.OnStart` begins.
2. Ensure the application-data directory exists and load/validate persistent state.
3. Create a queue item with `eventType = "Startup"`, current UTC occurrence time, and computer name.
4. Persist the queue item before attempting SMTP.
5. Start/ensure the 60-second retry timer.
6. Trigger queue processing on background work so `OnStart` is not blocked by network I/O.
7. Queue processing attempts the oldest pending item first.
8. `OnStart` completes without waiting indefinitely for SMTP.

If SMTP/config/network is unavailable, the Startup item remains queued.

## 9. ResumeAutomatic processing flow

The service enables Windows power-event handling and receives power status through `ServiceBase.OnPowerEvent`.

For each power notification:
1. If the status is not `PowerBroadcastStatus.ResumeAutomatic`, ignore it for notification purposes.
2. Do not create any task for `ResumeSuspend`.
3. For `ResumeAutomatic`, create one queue item with event type, UTC occurrence time, and computer name.
4. Persist the item before SMTP is attempted.
5. Trigger queue processing immediately in the background.
6. If the attempt fails, the normal 60-second retry loop owns subsequent retries.

There is no separate debounce/deduplication subsystem in the MVP. The explicit duplicate prevention rule is simply: handle `ResumeAutomatic`, not `ResumeSuspend`.

## 10. Notification email and SMTP success

Notification messages use fixed application-generated content only.

Suggested fixed subject:
- Startup: `BootWakeMailer: Startup - <ComputerName>`
- Resume: `BootWakeMailer: ResumeAutomatic - <ComputerName>`

The body contains only the event type, computer name, and event occurrence time. There is no configurable template.

SMTP sequence:
1. Load the latest `config.json` and unprotect the password with DPAPI.
2. Build one MailKit/MimeKit message using the configured sender and recipient.
3. Connect using the configured host, port, and security mode.
4. Authenticate with the configured single account.
5. Send the message.
6. Treat the task as successful when MailKit's send operation returns successfully after SMTP server acceptance.
7. Remove the task from `queue.json` and update `lastSuccessfulSendAtUtc`.

Final inbox delivery is not checked.

If SMTP acceptance succeeds but a later disconnect/cleanup operation fails, the notification task remains successful and must not be resent solely because cleanup failed. Any cleanup error may be recorded separately as the latest error.

## 11. Automatic retry and queue-processing flow

A single in-process synchronization gate shall serialize Startup, ResumeAutomatic, timer, and manual-retry processing.

The retry timer fires every 60 seconds while the service is running.
Each processing cycle:
1. Attempt to enter the synchronization gate; only one cycle may process the queue at a time.
2. Load `queue.json`.
3. Select the oldest pending item.
4. Increment `attemptCount`, set `lastAttemptAtUtc`, and persist the updated queue.
5. Attempt SMTP send using the latest configuration.
6. On success, remove the item, atomically save the queue, update `status.json`, then continue with the next item.
7. On failure, update the item's `lastError`, update `status.json`, persist both, and stop that processing cycle.
8. Release the synchronization gate.

Stopping the cycle after the first failure avoids repeatedly hammering the same unavailable SMTP server. The next timer tick or manual retry tries again.

There is no maximum retry count and no dead-letter queue in the MVP.

### Delivery semantics

The queue provides at-least-once behavior, not exactly-once behavior.

If the SMTP server accepts a message and the service crashes before the successful queue removal is persisted, that task can be sent again after restart. Preventing that narrow duplicate case would require additional protocol/idempotency machinery outside this MVP and is therefore intentionally not added.

## 12. WinForms and Windows Service interaction

There is no named-pipe protocol, socket, HTTP endpoint, Web API, or database.

The WinForms tool uses:
- `ServiceController` to read the BootWakeMailer service state.
- Read-only access to `queue.json` for pending count.
- Read-only access to `status.json` for latest success and latest error.
- Direct atomic write of `config.json` after validation and DPAPI protection.

### Immediate retry

Use one Windows Service custom command:
- Command ID: `128`.
- Meaning: retry pending queue now.
- WinForms calls `ServiceController.ExecuteCommand(128)`.
- The service handles it in `OnCustomCommand` and triggers the same serialized queue-processing path used by Startup, ResumeAutomatic, and the timer.

The WinForms tool never edits queue contents to force a retry.

### Configuration reload

No file watcher or reload protocol is needed. The service reads the current `config.json` before each SMTP attempt, so the next send/retry automatically uses the latest saved settings.

### Test email

The WinForms tool sends a test email directly through the shared MailKit sender using the values currently shown in the form.

A test email:
- Is not inserted into `queue.json`.
- Is not automatically retried.
- Shows its success/failure result directly in the WinForms UI.
- Does not update the service's `lastSuccessfulSendAtUtc`, because that field describes queued service notifications.

### Privilege model

To keep installation, ProgramData writes, DPAPI machine-scope access, and service custom commands simple, the configuration tool shall run elevated through its application manifest.

No custom service ACL/security-descriptor management is introduced in the MVP.

## 13. Installation and uninstallation

### Build/publish model

Both executable projects are published for `win-x64` as self-contained .NET 10 applications. The target Windows 11 machine does not need a separately installed .NET runtime.

The release payload contains the published service/config-tool files plus the four install/uninstall scripts.

### install.cmd / install.ps1

`install.cmd` is the double-click entry point and launches `install.ps1`.

`install.ps1` shall:
1. Ensure it is running with administrator privileges; request UAC elevation when needed.
2. Create `%ProgramFiles%\BootWakeMailer`.
3. Copy the self-contained published application files into that directory.
4. Create `%ProgramData%\BootWakeMailer` if missing.
5. Register service name `BootWakeMailer` with its executable under Program Files.
6. Configure the service startup type as Automatic.
7. Start the service.
8. Return a clear success/failure exit result.

No scheduled task, MSI, external installer framework, or updater is required.

### uninstall.cmd / uninstall.ps1

`uninstall.cmd` is the double-click entry point and launches `uninstall.ps1`.

`uninstall.ps1` shall:
1. Ensure it is running with administrator privileges; request UAC elevation when needed.
2. Stop the BootWakeMailer service if it is running.
3. Remove the Windows Service registration.
4. Remove `%ProgramFiles%\BootWakeMailer`.
5. Remove `%ProgramData%\BootWakeMailer`, including configuration, queue, and status files.
6. Return a clear success/failure exit result.

The MVP does not preserve application data after uninstall.

## 14. Error handling principles

1. Service callbacks, timer callbacks, and custom-command handlers must not allow recoverable exceptions to terminate the service process.
2. SMTP/network/configuration errors leave the notification task pending.
3. The newest operational error is written to `status.json`.
4. Per-task send errors are also stored on the corresponding queue item.
5. A missing queue file may be initialized as empty; a corrupt existing queue is not silently discarded.
6. Invalid or missing SMTP configuration is treated as a send failure, not as a reason to delete a task.
7. JSON writes use atomic replacement to reduce corruption risk.
8. Queue processing is serialized to avoid competing writes and duplicate concurrent sends.
9. Cleanup failures after confirmed SMTP acceptance must not convert an accepted notification back into a pending task.
10. Error messages should be concise and actionable; secrets, especially the SMTP password, must never be written to status/error text.
11. The MVP stores only the latest status error and per-task last error; it does not add a separate logging subsystem.

### Crash-window tradeoff

Persisting before send prevents task loss during ordinary SMTP/network failure. The tradeoff is the previously documented at-least-once behavior: a crash after SMTP acceptance but before queue removal can cause a duplicate on restart.

The MVP accepts this tradeoff rather than adding transactional storage or external idempotency infrastructure.

## 15. Explicitly outside the MVP

The architecture shall not add or prepare infrastructure for:
- Database or SQLite.
- Web API.
- Web UI.
- Multiple SMTP configurations.
- Multiple sender accounts.
- Multiple recipients.
- Configurable email templates.
- Automatic updates.
- Tray application.
- Cloud service.
- RabbitMQ.
- Redis.
- Docker.
- Complex layered/clean-architecture scaffolding.
- Distributed locks or distributed queueing.
- Exactly-once SMTP delivery guarantees.
- Inbox/final-delivery confirmation.
- `ResumeSuspend` handling.
- Boot-session identification or logic that distinguishes OS boot from service restart.
- Remote administration.
- Telemetry/analytics.

## 16. Implementation guardrails for later phases

When coding begins in a later phase:
- Implement only behavior described in `requirements.md` and this document.
- Prefer concrete small classes over abstraction layers created for hypothetical future features.
- Keep the service as the single writer for queue/status state.
- Reuse one shared SMTP implementation for service notifications and WinForms test mail.
- Do not start future-feature scaffolding “for extensibility”.
- Do not add business functionality unless the requirements are explicitly revised first.

This document intentionally defines the architecture only. No business code is produced in the current phase.
