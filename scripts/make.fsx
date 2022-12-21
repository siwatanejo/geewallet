#!/usr/bin/env fsharpi

open System
open System.IO
open System.Linq
open System.Diagnostics

open System.Text
open System.Text.RegularExpressions
#r "System.Core.dll"
open System.Xml
#r "System.Xml.Linq.dll"
open System.Xml.Linq
open System.Xml.XPath

#r "System.Configuration"
open System.Configuration
#load "InfraLib/Misc.fs"
#load "InfraLib/Process.fs"
#load "InfraLib/Git.fs"
open FSX.Infrastructure
open Process

#load "fsxHelper.fs"
open GWallet.Scripting

let UNIX_NAME = "gwallet"
let DEFAULT_FRONTEND = "GWallet.Frontend.Console"
let BACKEND = "GWallet.Backend"
let TEST_TYPE_UNIT = "Unit"
let TEST_TYPE_END2END = "End2End"

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let buildConfigFileName = "build.config"
let buildConfigContents =
    let buildConfig =
        Path.Combine (FsxHelper.ScriptsDir.FullName, buildConfigFileName)
        |> FileInfo
    if not (buildConfig.Exists) then
        let configureLaunch =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows -> ".\\configure.bat"
            | _ -> "./configure.sh"
        Console.Error.WriteLine (sprintf "ERROR: configure hasn't been run yet, run %s first"
                                         configureLaunch)
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwithf "All lines in %s must conform to format:\n\tkey=value"
                      buildConfigFileName
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in %s with a key '%s'."
                          buildConfigFileName key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libPrefixDir = DirectoryInfo (Path.Combine (prefix, "lib", UNIX_NAME))
let binPrefixDir = DirectoryInfo (Path.Combine (prefix, "bin"))

let launcherScriptFile =
    Path.Combine (FsxHelper.ScriptsDir.FullName, "bin", UNIX_NAME)
    |> FileInfo
let mainBinariesDir binaryConfig =
    Path.Combine (
        FsxHelper.RootDir.FullName,
        "src",
        DEFAULT_FRONTEND,
        "bin",
        binaryConfig.ToString())
    |> DirectoryInfo

let wrapperScript = """#!/usr/bin/env bash
set -eo pipefail

if [[ $SNAP ]]; then
    PKG_DIR=$SNAP/usr
    export MONO_PATH=$PKG_DIR/lib/mono/4.5
    export MONO_CONFIG=$SNAP/etc/mono/config
    export MONO_CFG_DIR=$SNAP/etc
    export MONO_REGISTRY_PATH=~/.mono/registry
    export MONO_GAC_PREFIX=$PKG_DIR/lib/mono/gac/
fi

DIR_OF_THIS_SCRIPT=$(dirname "$(realpath "$0")")
FRONTEND_PATH="$DIR_OF_THIS_SCRIPT/../lib/$UNIX_NAME/$GWALLET_PROJECT.exe"
exec mono "$FRONTEND_PATH" "$@"
"""

let RunNugetCommand (command: string) echoMode (safe: bool) =
    let nugetCmd =
        match Misc.GuessPlatform() with
        | Misc.Platform.Linux ->
            { Command = "mono"; Arguments = sprintf "%s %s" FsxHelper.NugetExe.FullName command }
        | _ ->
            { Command = FsxHelper.NugetExe.FullName; Arguments = command }
    if safe then
        Process.SafeExecute (nugetCmd, echoMode)
    else
        Process.Execute (nugetCmd, echoMode)

let PrintNugetVersion () =
    if not (FsxHelper.NugetExe.Exists) then
        false
    else
        let nugetProc = RunNugetCommand String.Empty Echo.Off false
        Console.WriteLine nugetProc.Output.StdOut
        if nugetProc.ExitCode = 0 then
            true
        else
            Console.Error.WriteLine nugetProc.Output.StdErr
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"

let BuildSolution
    (buildTool: string)
    (solutionFileName: string)
    (binaryConfig: BinaryConfig)
    (maybeConstant: Option<string>)
    (extraOptions: string)
    =
    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let defineConstantsFromBuildConfig =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> constants.Split([|";"|], StringSplitOptions.RemoveEmptyEntries) |> Seq.ofArray
        | None -> Seq.empty
    let allDefineConstants =
        match maybeConstant with
        | Some constant -> Seq.append [constant] defineConstantsFromBuildConfig
        | None -> defineConstantsFromBuildConfig
    let configOptions =
        if allDefineConstants.Any() then
            sprintf "%s;ExtraDefineConstants=%s" configOption (String.Join(";", allDefineConstants))
        else
            configOption
    let buildArgs = sprintf "%s %s %s"
                            solutionFileName
                            configOptions
                            extraOptions
    let buildProcess = Process.Execute ({ Command = buildTool; Arguments = buildArgs }, Echo.All)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

let JustBuild binaryConfig maybeConstant =
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    Console.WriteLine (sprintf "Building in %s mode..." (binaryConfig.ToString()))
    BuildSolution
        buildTool.Value
        // no need to pass solution file name because there's only 1 solution:
        String.Empty
        binaryConfig
        maybeConstant
        String.Empty

    Directory.CreateDirectory(launcherScriptFile.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$UNIX_NAME", UNIX_NAME)
                     .Replace("$GWALLET_PROJECT", DEFAULT_FRONTEND)
    File.WriteAllText (launcherScriptFile.FullName, wrapperScriptWithPaths)

let MakeCheckCommand (commandName: string) =
    if not (Process.CommandWorksInShell commandName) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontendBinariesDir (binaryConfig: BinaryConfig) =
    Path.Combine (FsxHelper.RootDir.FullName, "src", DEFAULT_FRONTEND, "bin", binaryConfig.ToString())

let GetPathToBackend () =
    Path.Combine (FsxHelper.RootDir.FullName, "src", BACKEND)

let MakeAll (maybeConstant: Option<string>) =
    let buildConfig = BinaryConfig.Debug
    JustBuild buildConfig maybeConstant
    buildConfig

let RunFrontend (buildConfig: BinaryConfig) (maybeArgs: Option<string>) =
    let monoVersion = Map.tryFind "MonoPkgConfigVersion" buildConfigContents

    let pathToFrontend = Path.Combine(GetPathToFrontendBinariesDir buildConfig, DEFAULT_FRONTEND + ".exe")

    let fileName, finalArgs =
        match maybeArgs with
        | None | Some "" -> pathToFrontend,String.Empty
        | Some args -> pathToFrontend,args

    let startInfo = ProcessStartInfo(FileName = fileName, Arguments = finalArgs, UseShellExecute = false)
    startInfo.EnvironmentVariables.["MONO_ENV_OPTIONS"] <- "--debug"

    let proc = Process.Start startInfo
    proc.WaitForExit()
    proc

let RunTests (suite: string) =
    let findTestAssembly theSuite =
        let testAssemblyName = sprintf "GWallet.Backend.Tests.%s" theSuite
        let testAssembly = Path.Combine(FsxHelper.RootDir.FullName, "src", testAssemblyName, "bin",
                                        testAssemblyName + ".dll") |> FileInfo
        if not testAssembly.Exists then
            failwithf "File not found: %s" testAssembly.FullName
        testAssembly

    // string*string means flag*value, e.g. "include" * "G2GFunder"
    let nunitCommandFor (testAssembly: FileInfo) (maybeArgs: Option<List<string*string>>): ProcessDetails =
        let convertArgsToString (charPrefixForFlag: char) =
            match maybeArgs with
            | None -> String.Empty
            | Some args ->
                sprintf "%s "
                    (String.Join (" ",
                                  args.Select(fun (flag,value) ->
                                    sprintf "%s%s %s" (charPrefixForFlag.ToString()) flag value
                                  )
                                 )
                    )

        match Misc.GuessPlatform() with
        | Misc.Platform.Linux ->
            let nunitCommand = "nunit-console"
            MakeCheckCommand nunitCommand

            let maybeExtraArgs = convertArgsToString '-'

            {
                Command = nunitCommand
                Arguments = sprintf "%s%s" maybeExtraArgs testAssembly.FullName
            }

        | _ ->
            if not FsxHelper.NugetExe.Exists then
                MakeAll None |> ignore

            let nunitVersion = "2.7.1"
            let runnerExe =
                Path.Combine (
                    FsxHelper.NugetScriptsPackagesDir().FullName,
                    sprintf "NUnit.Runners.%s" nunitVersion,
                    "tools",
                    "nunit-console.exe"
                ) |> FileInfo

            if not runnerExe.Exists then
                let installNUnitRunnerNugetCommand =
                    sprintf
                        "install NUnit.Runners -Version %s -OutputDirectory %s"
                        nunitVersion (FsxHelper.NugetScriptsPackagesDir().FullName)
                RunNugetCommand installNUnitRunnerNugetCommand Echo.All true
                    |> ignore

            let maybeExtraArgs = convertArgsToString '/'

            {
                Command = runnerExe.FullName
                Arguments = sprintf "%s%s" maybeExtraArgs testAssembly.FullName
            }

    let twoProcessTestNames =
        [
            "ChannelOpening"
            "HtlcPayment"
            "ChannelClosingAfterJustOpening"
            "ChannelClosingAfterSendingHTLCPayments"
            "ChannelLocalForceClosing"
            "ChannelRemoteForceClosingByFunder"
            "ChannelRemoteForceClosingByFundee"
            "Revocation"
            "HtlcPaymentRevocationCloseByFunder"
            "HtlcPaymentRevocationCloseByFundee"
            "CPFP"
            "MutualCloseCpfp"
            "UpdateFeeMsg"
        ]

    let geewallet2geewalletTestNamePrefix, funderSuffix, fundeeSuffix = "G2G_", "_Funder", "_Fundee"

    let runTwoProcessTest testAssembly testName =
        let funderRunnerCommand =
            nunitCommandFor testAssembly (
                Some [("include", geewallet2geewalletTestNamePrefix + testName + funderSuffix)]
            )

        let fundeeRunnerCommand =
            nunitCommandFor testAssembly (
                Some [("include", geewallet2geewalletTestNamePrefix + testName + fundeeSuffix)]
            )

        let funderRun = async {
            let res = Process.Execute(funderRunnerCommand, Echo.All)
            if res.ExitCode <> 0 then
                Console.Error.WriteLine (testName + "Funder test failed")
                Environment.Exit 1
        }

        let fundeeRun = async {
            let res = Process.Execute(fundeeRunnerCommand, Echo.All)
            if res.ExitCode <> 0 then
                Console.Error.WriteLine (testName + "Fundee test failed")
                Environment.Exit 1
        }

        [funderRun; fundeeRun]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

    let ourWalletToOurWalletEnd2EndTests () =
        let testAssembly = findTestAssembly TEST_TYPE_END2END

        for testName in twoProcessTestNames do
            runTwoProcessTest testAssembly testName

    Console.WriteLine (sprintf "Running %s tests..." suite)
    Console.WriteLine ()

    // so that we get file names in stack traces
    Environment.SetEnvironmentVariable("MONO_ENV_OPTIONS", "--debug")

    let testAssembly = findTestAssembly suite

    let allTwoProcessTestNamesToExclude =
        twoProcessTestNames
        |> Seq.ofList
        |> Seq.map
            (
                fun testName ->
                    [
                        geewallet2geewalletTestNamePrefix + testName + funderSuffix
                        geewallet2geewalletTestNamePrefix + testName + fundeeSuffix
                    ]
            )
        |> Seq.concat
        |> String.concat ","

    let runnerCommand =
        let maybeExcludeArgument =
            if suite = TEST_TYPE_END2END then
                Some [ ("exclude", allTwoProcessTestNamesToExclude) ]
            else
                None
        nunitCommandFor testAssembly maybeExcludeArgument

    let nunitRun = Process.Execute(runnerCommand, Echo.All)
    if nunitRun.ExitCode <> 0 then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

    if suite = TEST_TYPE_END2END then
        Console.WriteLine "First end2end tests finished running, now about to launch geewallet2geewallet ones..."
        ourWalletToOurWalletEnd2EndTests ()

let maybeTarget = GatherTarget (Misc.FsxArguments(), None)
match maybeTarget with
| None ->
    MakeAll None |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release None

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = (Misc.GetCurrentVersion FsxHelper.RootDir).ToString()

    let release = BinaryConfig.Release
    JustBuild release None
    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "%s.v.%s" UNIX_NAME version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend = GetPathToFrontendBinariesDir release
    let zipRun = Process.Execute({ Command = "cp"
                                   Arguments = sprintf "-rfvp %s %s" pathToFrontend pathToFolderToBeZipped },
                                 Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = { Command = zipCommand
                      Arguments = sprintf "%s -r %s %s"
                                      zipCommand zipName zipNameWithoutExtension }
    let zipRun = Process.Execute(zipLaunch, Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    RunTests TEST_TYPE_UNIT

| Some "check-end2end" ->
    RunTests "End2End"

| Some("install") ->
    let buildConfig = BinaryConfig.Release
    JustBuild buildConfig None

    let destDirUpperCase = Environment.GetEnvironmentVariable "DESTDIR"
    let destDirLowerCase = Environment.GetEnvironmentVariable "DestDir"
    let destDir =
        if not (String.IsNullOrEmpty destDirUpperCase) then
            destDirUpperCase |> DirectoryInfo
        elif not (String.IsNullOrEmpty destDirLowerCase) then
            destDirLowerCase |> DirectoryInfo
        else
            prefix |> DirectoryInfo

    let libDestDir = Path.Combine(destDir.FullName, "lib", UNIX_NAME) |> DirectoryInfo
    let binDestDir = Path.Combine(destDir.FullName, "bin") |> DirectoryInfo

    Console.WriteLine "Installing..."
    Console.WriteLine ()
    Misc.CopyDirectoryRecursively (mainBinariesDir buildConfig, libDestDir, [])

    let finalLauncherScriptInDestDir = Path.Combine(binDestDir.FullName, launcherScriptFile.Name) |> FileInfo
    if not (Directory.Exists(finalLauncherScriptInDestDir.Directory.FullName)) then
        Directory.CreateDirectory(finalLauncherScriptInDestDir.Directory.FullName) |> ignore
    File.Copy(launcherScriptFile.FullName, finalLauncherScriptInDestDir.FullName, true)
    if Process.Execute({ Command = "chmod"; Arguments = sprintf "ugo+x %s" finalLauncherScriptInDestDir.FullName },
                        Echo.Off).ExitCode <> 0 then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let buildConfig = MakeAll None
    RunFrontend buildConfig None
        |> ignore

| Some "update-servers" ->
    let buildConfig = MakeAll None
    Directory.SetCurrentDirectory (GetPathToBackend())
    let proc1 = RunFrontend buildConfig (Some "--update-servers-file")
    if proc1.ExitCode <> 0 then
        Environment.Exit proc1.ExitCode
    else
        let proc2 = RunFrontend buildConfig (Some "--update-servers-stats")
        Environment.Exit proc2.ExitCode

| Some "strict" ->
    MakeAll <| Some "STRICTER_COMPILATION_BUT_WITH_REFLECTION_AT_RUNTIME"
        |> ignore

| Some "sanitycheck" ->

    if not FsxHelper.NugetExe.Exists then
        MakeAll None |> ignore

    let microsoftBuildLibVersion = "16.11.0"
    let installMicrosoftBuildLibRunnerNugetCommand =
        sprintf
            "install Microsoft.Build -Version %s -OutputDirectory %s"
            microsoftBuildLibVersion (FsxHelper.NugetScriptsPackagesDir().FullName)
    RunNugetCommand installMicrosoftBuildLibRunnerNugetCommand Echo.All true
        |> ignore

    let sanityCheckScript = Path.Combine(FsxHelper.ScriptsDir.FullName, "sanitycheck.fsx")
    Process.SafeExecute (
        {
            Command = FsxHelper.FsxRunner
            Arguments = sanityCheckScript
        },
        Echo.All
    )
    |> ignore

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2
