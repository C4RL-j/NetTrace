Run the App Usage 005.1 installer tests from an ordinary, unelevated terminal:

```powershell
dotnet run --project tests/NetTrace.AppUsageInstallerTests/NetTrace.AppUsageInstallerTests.csproj
```

The suite supplies a fake installer platform. It verifies production configuration,
create/update decisions, foreign-service refusal, recovery, uninstall ordering,
database preservation, argument and elevation checks, and rollback after failed
new installs or updates. It does not access SCM, ETW, the real ProgramData database,
or privileged file permissions. Four direct CLI checks exercise help and invalid
or normal-token invocation; elevation rejection precedes any system operation.
Actual installation, startup, normal-client IPC,
restart recovery, and removal belong to the explicit production live validation.
