# envsync

A cross-platform (Windows, Linux and macOS) command-line tool that connects to **Azure Key Vault**, **AWS Secrets Manager** and
**HashiCorp Vault** and hands the secrets to your program **without plain-text `.env` files**.
It also warns you, before anything runs, if a required key is missing.

```text
envsync run -- dotnet run                      # secrets exist only in the child process's environment
envsync env --shell pwsh | Invoke-Expression   # or as session variables in your current shell
envsync check                                  # is everything I need there? (handy in CI)
```

## Why

A `.env` copied from machine to machine ends up in a chat, in a backup or in a commit. With envsync the repository carries only a
manifest (`envsync.json`) with **references**, and the values travel from the secret manager to your process's memory.

## Installation

Requires the **.NET 10** SDK or runtime.

```bash
dotnet pack src/EnvSync.Cli -c Release -o artifacts
dotnet tool install --global --add-source ./artifacts EnvSync.Tool
envsync --version
```

## Manifest

`envsync.json` is searched for upwards from the current directory (the way git looks for `.git`) or given with `--manifest`.
It accepts comments and trailing commas. A complete example lives in [`examples/envsync.json`](examples/envsync.json).

```jsonc
{
  "defaultProfile": "dev",
  "profiles": {
    "dev": {
      "providers": {
        "kv":    { "type": "azure-keyvault",  "uri": "https://my-vault.vault.azure.net" },
        "vault": { "type": "hashicorp-vault", "address": "https://vault.example.com:8200" }
      },
      "variables": {
        "DB_PASSWORD": { "from": "kv",    "ref": "db-password" },
        "JWT_SECRET":  { "from": "vault", "ref": "app#jwt" },
        "LOG_LEVEL":   { "from": "kv",    "ref": "log-level", "required": false }
      }
    }
  }
}
```

**References (`ref`)**: `name` or `name#field`. With `#field`, one property of a JSON secret is extracted.

| Provider (`type`) | Settings | Notes |
|---|---|---|
| `azure-keyvault` | `uri` (https) | The secret name accepts only letters, digits and `-`. `DB_PASSWORD` does not exist in Key Vault: the `ref` is the name **in the vault**. Only Key Vault hosts are accepted (`*.vault.azure.net` and the sovereign-cloud equivalents). |
| `aws-secrets` | `region` (or `AWS_REGION`), `profile` (optional) | Accepts a name or an ARN. Binary secrets are not supported. SSO and role profiles are supported. |
| `hashicorp-vault` | `address` (or `VAULT_ADDR`), `namespace`, `mount` (default `secret`), `kv` (`1` or `2`, default `2`) | The path is **relative to the `mount`**. Without `#field` it is an error, because a Vault secret has several fields. See "Where your token goes". |

Unknown or blank settings are an error: they are neither ignored nor replaced by a default. Every manifest error is reported at once,
each with its JSON path.

**An empty secret is not a value.** If a secret exists but is empty, it counts as missing: a required variable fails (exit code 11).

**Optional variables** (`"required": false`): they are skipped only if the secret **does not exist** or is empty, with a warning that
says why. A rejected credential, a timeout, a JSON of an unexpected shape or an invalid configuration **are still errors**:
it never starts half-configured. A skipped optional variable is **removed** from the child's environment, so a value inherited from
another session cannot survive.

**Reserved names.** A secret manager cannot set variables that load code or reconfigure a shell: `PATH`, `PATHEXT`, `COMSPEC`,
`PSModulePath`, `IFS`, `ENV`, `BASH_ENV`, `PROMPT_COMMAND`, `PS0`-`PS4`, `NODE_OPTIONS`, `PYTHONPATH`, `JAVA_TOOL_OPTIONS`,
`DOTNET_STARTUP_HOOKS`, `GIT_SSH_COMMAND`, anything starting with `LD_` or `DYLD_`, among others. Case-insensitive.
Two names that differ only in case count as duplicates on every operating system, because Windows would merge them.

## Where your token goes

A manifest is repository content: anyone with write access, or a PR, could put the address of their own server in it and receive the
token you hold for *your* Vault. That is why **the manifest does not decide where your credentials go**:

- **Vault**: the manifest's `address` is accepted only if it is *loopback* (a local development server), has the same origin as your
  own `VAULT_ADDR`, or its host is in `ENVSYNC_TRUSTED_HOSTS`. With only a `VAULT_TOKEN`, a manifest that points elsewhere fails with
  a message explaining how to confirm it. Without an `address` in the manifest, your `VAULT_ADDR` is used.
- **Azure**: the credential is only offered to Key Vault or Managed HSM hosts.
- **`ENVSYNC_TRUSTED_HOSTS`**: exact hosts or `*.domain`, separated by commas, semicolons or spaces. It is a variable of **your**
  environment, which the repository cannot touch. `*.corp.example.com` covers subdomains, not `corp.example.com` nor `evilcorp.example.com`.

## Commands

Common options: `--manifest`, `--profile` (or `ENVSYNC_PROFILE`), `--timeout <s>` (15, max 86400), `--concurrency <n>` (8, max 256), `--verbose`.
An **empty** value for `--manifest`, `--profile` or `ENVSYNC_PROFILE` is a usage error, not "not given": an undefined `STAGE` in CI
must not silently fall back to the default profile. A profile chosen by default is announced on stderr.

### `envsync run -- program args...`

Launches the program with the secrets in **its** environment and nowhere else. It inherits stdin/stdout/stderr, so it keeps the
terminal. Its exit code passes through untouched. If a required key is missing it is **not launched** and what is missing is explained.

**The `--` is mandatory.** Without it, any `-v`, `-p` or `-m` that the program carries after its name would be read as envsync options
and silently removed from its arguments: `envsync run -- npm --version`.

On Windows, `.cmd` and `.bat` files (`npm.cmd`, `az.cmd`, `mvn.cmd`...) are found thanks to `PATHEXT`, but cmd.exe **reinterprets** their
arguments with its own rules: `&` starts another command and `%VAR%` is expanded (with the secrets just injected). An argument containing
`" % & | < > ^ ! ( )` or a line break is **rejected** for those targets; `.exe` files have no such restriction.

`run` inherits your whole environment, including the providers' credentials (`VAULT_TOKEN`, `AZURE_CLIENT_SECRET`,
`AWS_SECRET_ACCESS_KEY`), because many programs need the same AWS or Azure credentials. Remove them yourself if the child must not see them.

### `envsync env --shell <pwsh|powershell|bash|zsh|cmd>`

Prints a script that sets the variables in the current session. Default: PowerShell on Windows, bash everywhere else.

```powershell
envsync env --shell pwsh | Invoke-Expression        # PowerShell 7 and Windows PowerShell 5.1
```
```bash
eval "$(envsync env --shell bash)"                  # bash and zsh
```
```bat
for /f "usebackq delims=" %L in (`envsync env --shell cmd`) do %L      :: cmd; in a .cmd file write %%L
```

`envsync` must be on the `PATH`: cmd strips only one pair of outer quotes, so the command inside cannot carry any.
This is the idiom that **never touches the disk**: `for /f` executes each line envsync prints, and the secrets go from its stdout to the console itself.

- **It fails loudly.** `eval` of empty output succeeds, so if the environment cannot be built (a missing key, an invalid manifest,
  even a typo in the options), the only thing printed on stdout is **a statement that makes whoever evaluates it fail** with the same
  exit code: `(exit 11)` in bash and zsh, `throw '...'` in PowerShell, `cmd /c exit 11` in cmd.
  Never a partial script and never a secret. With `set -e` the script stops; without it, check `$?`.
  An unsupported shell receives nothing, because its syntax is unknown.
- **It refuses to print to a terminal** (the secrets would stay in the screen history); pipe it, or pass
  `--unsafe-print`. That refusal is decided before any secret is requested. stdout carries **only** the script; everything else goes to stderr.
- **PowerShell receives pure ASCII**: PowerShell decodes a native command's output with the console code page, not UTF-8, and a
  non-ASCII character could change value or even cut the string short. Everything that is not printable ASCII is written as `[char]N`.
- **`cmd` is the most restrictive**: printable ASCII only, without `% " ! ^`, and statements shorter than 8000 characters (cmd cuts the
  line at 8191). A value that does not fit is rejected (exit code 14) instead of being escaped "by eye"; `run` does not go through a shell and can carry it.
- In PowerShell and `cmd`, assigning an **empty** value deletes the variable on Windows (which is why an empty secret is already an error).
- A skipped optional variable is **not** removed from your shell (`env` cannot do that): the warning says so.

### `envsync check [--offline] [--format text|json]`

Checks that every key is there and **never prints values**. With `--offline` it only validates the manifest and the providers' settings,
with no requests to the secret managers: exit code 0 then means "valid configuration", **not** "the keys exist"
(the JSON says so with `"verified": false`). On failure, besides the report on stdout it writes a summary on stderr.

```text
Profile 'dev'
VARIABLE     PROVIDER  STATUS   DETAIL
DB_PASSWORD  kv        OK
JWT_SECRET   vault     MISSING  Secret 'app' was not found under mount 'secret'.
LOG_LEVEL    kv        SKIPPED  optional, left unset: Secret 'log-level' was not found in the vault.
3 variable(s): 1 ready, 1 missing, 0 failed, 1 skipped.
```

## Authentication

envsync **stores no credentials**; it uses each manager's own:

- **Azure**: `DefaultAzureCredential` (`AZURE_*` variables, managed identity, Azure CLI, Visual Studio...). Try `az login`.
- **AWS**: the SDK's default chain (environment, `~/.aws`, SSO, roles) or a named `profile`, including SSO and role profiles.
- **Vault**: `VAULT_TOKEN` or the `~/.vault-token` file that `vault login` writes.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything is fine (in `run`, the child program's code) |
| 10 | Invalid manifest or configuration |
| 11 | Required keys are missing (they do not exist or are empty) |
| 12 | Authentication, network or provider failure |
| 13 | Internal error, including an unexpected exception from a provider (not a network outage: retrying is pointless) |
| 14 | A secret exists, but the requested shell cannot carry its value safely |
| 64 | Incorrect command-line usage |
| 126 / 127 | The program of `run` could not be started / was not found |
| 130 | Cancelled by the user |

envsync's own codes are 10-14 so they do not collide with those already used by most programs (0, 1, 2).

## Security model and limits

What **is** guaranteed (with tests):

- Secrets are never written to disk. `SecretValue` always displays as `[REDACTED]`, so a log or an exception cannot leak it.
- Anything that comes from a manifest or a provider is **sanitized** before it is shown: control characters, line breaks, escape
  sequences and bidirectional overrides are replaced, so they cannot erase or forge lines in a terminal or a CI log.
- Providers' error messages are kept whole (on one line and bounded), because they usually carry exactly what needs fixing;
  they may name resources (account IDs, roles), **never secret values**.
- The Vault token is never followed across a redirect, never travels over `http://` (except `localhost`), and the manifest does not choose where it goes.
- Vault responses are limited to 1 MiB, and so is the manifest.
- The shell emitters are verified against **real** bash, PowerShell 7, Windows PowerShell 5.1 and cmd: 33 hostile values in bash
  and PowerShell and 11 in cmd (the subset it accepts), checking in each that the value comes back identical and that nothing is executed;
  PowerShell also **through a real native pipe**, with the default console encoding.

What it **cannot** do, and is worth knowing:

- Environment variables are visible to other processes **of the same user** (`/proc/<pid>/environ`, debuggers). This is inherent to the
  mechanism; what is avoided is the file on disk, not exposure to the user themselves.
- A .NET `string` cannot be erased from memory. The tool's own buffers (script, transcoding) are cleared, but the values
  stay in envsync's memory while `run` executes: envsync waits for the child and keeps the references.
- **Signals.** Ctrl+C also reaches the child (same process group) and envsync waits up to 10 s for it to finish on its own before stopping it.
  **SIGTERM and SIGHUP are not forwarded to the child**: on receiving them, envsync waits those 10 s and then kills it. In a container
  (`docker stop`) the child gets no chance to shut down gracefully. Behavior with real signals has not been verified, only the
  cancellation logic with tests.
- The manifest is searched for upwards to the root, like git: do not run envsync in a shared directory where another user may
  have left an `envsync.json`.
- `--offline` makes no requests to the secret managers, but the AWS SDK may read your local credential files when it is
  constructed; it has not been checked with a network capture that nothing else leaves the machine.
- Git Bash (Cygwin) drops a raw `\r` when reading a script, even between quotes; the bash emitter writes it as `$'\r'`.

## Architecture

```text
Domain          pure records and value types (SecretValue, SecretReference, Profile, Result<T>)
Application     CQRS: queries and commands as `readonly record struct`, ValueTask handlers, ports
Infrastructure  manifest parser, shell emitters, process launcher
Providers.*     Azure, AWS and Vault, each in its own project (the heavy SDKs stay isolated)
Cli             System.CommandLine and the composition root (hand-written DI, no container and no reflection)
```

- **CQRS without a mediator**: `ResolveEnvironmentQuery`, `CheckRequirementsQuery`, `ExportEnvironmentQuery` and `RunProcessCommand` are
  `readonly record struct`; the handlers implement `IQueryHandler` / `ICommandHandler`. They are passed by value because an `async` method
  cannot take `in` parameters.
- **"Zero allocation" with an honest scope**: it holds, and it is **verified** with `GC.GetAllocatedBytesForCurrentThread`, in
  reference parsing, name validation, the three shell emitters and the pooled buffer. It does not hold, and cannot, in the SDKs'
  network calls nor when the final secret value is materialized as a `string`.
- **Bounded concurrent resolution**: one provider per alias, a cap on simultaneous requests, a per-secret timeout and a
  preallocated array where each task writes its own slot (no locks, stable order). A factory or a `Dispose` that throws does not break the run.
- **Native AOT, verified on Windows**: the trimming/AOT analyzers are active across `src/` with warnings as errors, the tool's own
  code uses no reflection and no assembly scanning, and `dotnet publish src/EnvSync.Cli -r win-x64 -p:PublishAot=true` produces
  a single 16.5 MB native binary **with no warnings at all** and without needing the .NET runtime. The end-to-end tests pass
  against that binary (`ENVSYNC_E2E_BINARY=<path>`). It needs the operating system's C++ tools (Visual Studio on Windows, `clang` on Linux);
  on Windows `vswhere` must be on the `PATH`. **Linux and macOS have not been tried here**: the CI's `aot` job does that.

## Development

```bash
dotnet build envsync.slnx
dotnet test --solution envsync.slnx
```

> **Do not pass `--nologo` to `dotnet test`.** On SDK 10 it is forwarded to the xUnit v3 host, which rejects it, and the misleading result is
> "Zero tests ran" with exit code 5.

- Tests use xUnit v3 on Microsoft.Testing.Platform, hand-written test doubles (no mocking libraries) and test-driven development.
- Tests against real shells are **skipped**, not simulated, if the shell is not installed on the machine.
- `tests/EnvSync.Cli.Tests/EndToEndTests.cs` runs the real binary against a fake Vault on loopback and a real child process, and includes
  cmd's `for /f` idiom against a real `cmd.exe`.
- These tests have only been run on Windows. The matrix in `.github/workflows/ci.yml` is meant to run them on Linux and macOS,
  and its `aot` job publishes the native binary on each OS and runs the end-to-end tests against it.
