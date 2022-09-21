namespace GWallet.Backend.Tests.Unit

open System

open NUnit.Framework

open GWallet.Backend
open GWallet.Backend.UtxoCoin.Lightning


[<TestFixture>]
type RapidGossipSyncer() =

    static let syncData =
        lazy(
            use httpClient = new Net.Http.HttpClient()
            let baseUrl = "https://github.com/nblockchain/geewallet-binary-dependencies/raw/master/tests/RGS/"
            let full = httpClient.GetByteArrayAsync(baseUrl + "rgs_full") |> Async.AwaitTask
            let incremental = httpClient.GetByteArrayAsync(baseUrl + "rgs_incr_1663545600") |> Async.AwaitTask
            FSharpUtil.AsyncExtensions.MixedParallel2 full incremental
            |> Async.RunSynchronously
        )

    [<Test>]
    member __.deserialization() =
        // Regression test for sync data deserialization and graph updating
        let fullData, incrementalData = syncData.Force()

        let initialRoutingState = RapidGossipSyncer.GetRoutingState()
        Assert.AreEqual(initialRoutingState.LastSyncTimestamp, 0u)
        Assert.AreEqual(initialRoutingState.Graph.EdgeCount, 0)

        RapidGossipSyncer.SyncUsingData fullData |> Async.RunSynchronously
        let routingStateAfterFullSync = RapidGossipSyncer.GetRoutingState()
        Assert.AreEqual(routingStateAfterFullSync.LastSyncTimestamp, 1663545600u)
        Assert.AreEqual(routingStateAfterFullSync.Graph.EdgeCount, 175343)

        RapidGossipSyncer.SyncUsingData incrementalData |> Async.RunSynchronously
        let routingStateAfterIncrementalSync = RapidGossipSyncer.GetRoutingState()
        Assert.AreEqual(routingStateAfterIncrementalSync.LastSyncTimestamp, 1663632000u)
        Assert.AreEqual(routingStateAfterIncrementalSync.Graph.EdgeCount, 176044)
