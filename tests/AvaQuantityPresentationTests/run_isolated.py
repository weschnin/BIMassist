"""Run one dotnet invocation with separate, native scratch settings paths."""
import os
from pathlib import Path
import subprocess
import sys
import uuid

scratch = Path.home() / "AppData/Local/hermes/cache/scratch" / ("ava-presentation-" + uuid.uuid4().hex)
scratch.mkdir(parents=True)
env = os.environ.copy()
env.update(BIMASSIST_AVA_WORKSPACE_SETTINGS=str(scratch / "workspace.json"),
           BIMASSIST_AVA_PARAMETER_CHOICES=str(scratch / "parameters.json"),
           TMPDIR=str(scratch), TEMP=str(scratch), TMP=str(scratch),
           AVA_PRESENTATION_TEST_OUTPUT=str(scratch))
args = sys.argv[1:] or ["run", "--project", "tests/AvaQuantityPresentationTests/AvaQuantityPresentationTests.csproj", "-c", "Release"]
result = subprocess.run(["dotnet", *args], env=env, text=True, capture_output=True, encoding="utf-8", errors="replace")
log = scratch / "dotnet.log"
log.write_text(result.stdout + result.stderr, encoding="utf-8")
print(result.stdout + result.stderr)
print(f"EXIT={result.returncode} LOG={log} SCRATCH={scratch}")
sys.exit(result.returncode)
