#pragma once

// Mono builds publish managed code with the PackageProjectMono automation command
// (reverse flow, no ArchiveDirectory); CoreCLR builds keep the upstream PackageProject.
namespace UnrealSharp::BuildAction
{
	inline constexpr const TCHAR* GenerateProject = TEXT("GenerateProject");
	inline constexpr const TCHAR* GenerateUserSolution = TEXT("GenerateUserSolution");
	inline constexpr const TCHAR* BuildEmitLoadOrder = TEXT("BuildEmitLoadOrder");
	inline constexpr const TCHAR* BuildUserSolution = TEXT("BuildUserSolution");
#if UNREALSHARP_MONO
	inline constexpr const TCHAR* PackageProject = TEXT("PackageProjectMono");
#else
	inline constexpr const TCHAR* PackageProject = TEXT("PackageProject");
#endif
}
