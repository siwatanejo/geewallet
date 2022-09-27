namespace GWallet.Backend.UtxoCoin

open System
open System.Net
open System.Text.RegularExpressions
open System.Diagnostics

open NOnion
open NOnion.Directory
open NOnion.Services

open GWallet.Backend
open System.Net.Sockets


module internal TorOperations =
    let GetRandomTorFallbackDirectoryServer() =
        match Caching.Instance.GetServers
            (ServerType.ProtocolServer ServerProtocol.Tor)
            |> Shuffler.Unsort
            |> Seq.tryHead with
        | Some server -> server
        | None ->
            failwith "Couldn't find any Tor server"
    
    let BootstrapWithMeasurment(server:ServerDetails): Async<TorDirectory> =
        let endpoint = 
            match server.ServerInfo.ConnectionType.Protocol with
            | Protocol.Tcp port ->
                IPEndPoint(IPAddress.Parse server.ServerInfo.NetworkPath, int32 port)
            | _ -> failwith "Invalid Tor directory. Tor directories must have an IP and port."
        async {
            let stopwatch = Stopwatch()
            stopwatch.Start()

            try
                let! directory = TorDirectory.Bootstrap (endpoint)
                stopwatch.Stop()
                let historyFact = { TimeSpan = stopwatch.Elapsed; Fault = None }
                Caching.Instance.SaveServerLastStat 
                    (fun srv -> srv = server)
                    historyFact
                return directory
            with
            | ex ->
                stopwatch.Stop()
                let exInfo =
                    {
                        TypeFullName = ex.GetType().FullName
                        Message = ex.Message
                    }
                let historyFact = { TimeSpan = stopwatch.Elapsed; Fault = Some(exInfo) }
                Caching.Instance.SaveServerLastStat 
                    (fun srv -> srv = server)
                    historyFact
                return raise <| FSharpUtil.ReRaise ex 
        }

    let GetTorDirctoryForServer(server:ServerDetails): Async<TorDirectory> = 
        async {
            return! FSharpUtil.Retry<TorDirectory, NOnionException, SocketException>
                (fun _ -> 
                    BootstrapWithMeasurment server
                )
                Config.TOR_CONNECTION_RETRY_COUNT
        }

    let internal GetTorDirectory(): Async<TorDirectory> =
        async {
            return! FSharpUtil.Retry<TorDirectory, NOnionException, SocketException>
                (fun _ -> 
                    let randomServer = GetRandomTorFallbackDirectoryServer()
                    BootstrapWithMeasurment(randomServer)
                )
                Config.TOR_CONNECTION_RETRY_COUNT
        }

    let internal StartTorServiceHost directory =
        async {
            return! FSharpUtil.Retry<TorServiceHost, NOnionException, SocketException>
                (fun _ -> async { 
                    let torHost = TorServiceHost(directory, Config.TOR_CONNECTION_RETRY_COUNT) 
                    do! torHost.Start()
                    return torHost
                })
                Config.TOR_CONNECTION_RETRY_COUNT
        }

    let internal TorConnect directory introductionPoint =
        async {
            return! FSharpUtil.Retry<TorServiceClient, NOnionException, SocketException>
                (fun _ -> TorServiceClient.Connect directory introductionPoint)
                Config.TOR_CONNECTION_RETRY_COUNT
        }

    let internal ExtractServerListFromGithub() : List<(string*string)> =
        let urlToTorServerList = "https://raw.githubusercontent.com/torproject/tor/main/src/app/config/fallback_dirs.inc"
        use webClient = new WebClient()
        let fetchedInfo: string = webClient.DownloadString urlToTorServerList

        let ipv4Pattern: string = "\"([0-9\.]+)\sorport=(\S*)\sid=(\S*)\""
        let matches = Regex.Matches(fetchedInfo, ipv4Pattern)

        matches
        |> Seq.cast
        |> Seq.map (fun (regMatch: Match) ->
            (regMatch.Groups.[1].Value, regMatch.Groups.[2].Value))
        |> Seq.toList
