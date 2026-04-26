namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Resolves additional assemblies the code executor should reference when
/// running scripts against a workspace whose runtime types live outside
/// the analyzed source — Unity build artifacts (UnityEngine.dll,
/// UnityEditor.dll, generated Player.dll), ASP.NET runtime packs, etc.
/// Without this port, <c>lifeblood_execute</c> can resolve a
/// project-defined type, but a script that touches a Unity engine type
/// fails compilation because the host BCL doesn't carry it.
///
/// Pure read port. Returns the list of probe paths and any diagnostics
/// the resolver wants to surface ("no Unity build artifacts found at
/// Library/Bee/artifacts. Run a Unity build first.") so the tool layer
/// can echo them to the caller. The resolver does NOT load the
/// assemblies — it just reports paths. The executor decides what to do.
///
/// Closes LB-BUG-014. See INV-EXECUTE-001 in CLAUDE.md.
/// </summary>
public interface IRuntimeAssemblyResolver
{
    /// <summary>
    /// Absolute paths of additional assemblies the executor should
    /// reference. Empty when no probe directory was found or all
    /// directories were empty. Caller must filter out paths it cannot
    /// access — the resolver returns paths optimistically.
    /// </summary>
    string[] GetAssemblyProbePaths();

    /// <summary>
    /// Diagnostics worth surfacing to the caller — typically empty.
    /// Populated when the resolver expected to find something and
    /// didn't (Unity workspace with no build artifacts, runtime pack
    /// path missing, etc.). Each entry is a single-line operator-
    /// readable message; never null.
    /// </summary>
    string[] GetDiagnostics();
}
