// because of the use of internal AcceptCloseChannel and ReceiveMonoHopPayment
#nowarn "44"

namespace GWallet.Backend.Tests.End2End

open System
open System.IO
open System.Threading

open NUnit.Framework
open NBitcoin
open DotNetLightning.Payment
open DotNetLightning.Utils
open ResultUtils.Portability

open GWallet.Backend
open GWallet.Backend.UtxoCoin
open GWallet.Backend.UtxoCoin.Lightning
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks


[<TestFixture>]
type LN() =
    do Config.SetRunModeToTesting()

    let walletToWalletTestPayment1Amount = Money (0.01m, MoneyUnit.BTC)
    let walletToWalletTestPayment2Amount = Money (0.015m, MoneyUnit.BTC)

    let TearDown walletInstance bitcoind electrumServer lnd =
        (walletInstance :> IDisposable).Dispose()
        (lnd :> IDisposable).Dispose()
        (electrumServer :> IDisposable).Dispose()
        (bitcoind :> IDisposable).Dispose()

    let OpenChannelWithFundee (nodeOpt: Option<NodeEndPoint>) =
        async {
            let! clientWallet = ClientWalletInstance.New None
            let! bitcoind = Bitcoind.Start()
            let! electrumServer = ElectrumServer.Start bitcoind
            let! lnd = Lnd.Start bitcoind

            do! clientWallet.FundByMining bitcoind lnd

            let! lndEndPoint = lnd.GetEndPoint()

            let nodeEndPoint =
                match nodeOpt with
                | None -> lndEndPoint
                | Some node -> node

            let! channelId, fundingAmount = clientWallet.OpenChannelWithFundee bitcoind nodeEndPoint

            let channelInfoAfterOpening = clientWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterOpening.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            return channelId, clientWallet, bitcoind, electrumServer, lnd, fundingAmount
        }

    let AcceptChannelFromLndFunder () =
        async {
            let! serverWallet = ServerWalletInstance.New Config.FundeeLightningIPEndpoint None
            let! bitcoind = Bitcoind.Start()
            let! electrumServer = ElectrumServer.Start bitcoind
            let! lnd = Lnd.Start bitcoind

            do! lnd.FundByMining bitcoind

            let! feeRate = ElectrumServer.EstimateFeeRate()
            let fundingAmount = Money(0.1m, MoneyUnit.BTC)
            let acceptChannelTask = Lightning.Network.AcceptChannel serverWallet.NodeServer
            let openChannelTask = async {
                match serverWallet.NodeEndPoint with
                | EndPointType.Tcp endPoint ->
                    do! lnd.ConnectTo endPoint
                    return!
                        lnd.OpenChannel
                            endPoint
                            fundingAmount
                            feeRate
                | EndPointType.Tor _torEndPoint ->
                    return failwith "unreachable because tests use TCP"
            }

            let! acceptChannelRes, openChannelRes = AsyncExtensions.MixedParallel2 acceptChannelTask openChannelTask
            let (channelId, _) = UnwrapResult acceptChannelRes "AcceptChannel failed"
            UnwrapResult openChannelRes "lnd.OpenChannel failed"

            // Wait for the funding transaction to appear in mempool
            while bitcoind.GetTxIdsInMempool().Length = 0 do
                Thread.Sleep 500

            // Mine blocks on top of the funding transaction to make it confirmed.
            let minimumDepth = BlockHeightOffset32 6u
            bitcoind.GenerateBlocksToDummyAddress minimumDepth

            do! serverWallet.WaitForFundingConfirmed channelId

            let initialInterval = TimeSpan.FromSeconds 1.0

            let rec tryAcceptLock (backoff: TimeSpan) =
                async {
                    let! lockFundingRes = Lightning.Network.AcceptLockChannelFunding serverWallet.NodeServer channelId
                    match lockFundingRes with
                    | Error error ->
                            let backoffMillis = (int backoff.TotalMilliseconds)
                            Infrastructure.LogDebug <| SPrintF1 "accept error: %s" error.Message
                            Infrastructure.LogDebug <| SPrintF1 "retrying in %ims" backoffMillis
                            do! Async.Sleep backoffMillis
                            return! tryAcceptLock (backoff + backoff)
                    | Ok _ ->
                        return ()
                }

            do! tryAcceptLock initialInterval

            let channelInfo = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfo.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            return channelId, serverWallet, bitcoind, electrumServer, lnd
        }

    let AcceptChannelFromGeewalletFunder () =
        async {
            let! serverWallet = ServerWalletInstance.New Config.FundeeLightningIPEndpoint (Some Config.FundeeAccountsPrivateKey)
            let! pendingChannelRes =
                Lightning.Network.AcceptChannel
                    serverWallet.NodeServer

            let (channelId, _) = UnwrapResult pendingChannelRes "OpenChannel failed"

            let! lockFundingRes = Lightning.Network.AcceptLockChannelFunding serverWallet.NodeServer channelId
            UnwrapResult lockFundingRes "LockChannelFunding failed"

            let channelInfo = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfo.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            if Money(channelInfo.Balance, MoneyUnit.BTC) <> Money(0.0m, MoneyUnit.BTC) then
                return failwith "incorrect balance after accepting channel"

            return serverWallet, channelId
        }

    let ClientCloseChannel (clientWallet: ClientWalletInstance) (bitcoind: Bitcoind) channelId =
        async {
            let! closeChannelRes = Lightning.Network.CloseChannel clientWallet.NodeClient channelId None
            match closeChannelRes with
            | Ok _ -> ()
            | Error err -> return failwith (SPrintF1 "error when closing channel: %s" (err :> IErrorMsg).Message)

            match (clientWallet.ChannelStore.ChannelInfo channelId).Status with
            | ChannelStatus.Closing -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Closing, got %A" status)

            // Give fundee time to see the closing tx on blockchain
            do! Async.Sleep 10000

            // Mine 10 blocks to make sure closing tx is confirmed
            bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 (uint32 10))

            let rec waitForClosingTxConfirmed attempt = async {
                Infrastructure.LogDebug (SPrintF1 "Checking if closing tx is finished, attempt #%d" attempt)
                if attempt = 10 then
                    return Error "Closing tx not confirmed after maximum attempts"
                else
                    let! closingTxResult = Lightning.Network.CheckClosingFinished clientWallet.ChannelStore channelId
                    match closingTxResult with
                    | Tx (Full, _closingTx) ->
                        return Ok ()
                    | _ ->
                        do! Async.Sleep 1000
                        return! waitForClosingTxConfirmed (attempt + 1)
            }

            let! closingTxConfirmedRes = waitForClosingTxConfirmed 0
            match closingTxConfirmedRes with
            | Ok _ -> ()
            | Error err -> return failwith (SPrintF1 "error when waiting for closing tx to confirm: %s" err)
        }

    let SendHtlcPaymentsToLnd (clientWallet: ClientWalletInstance)
                              (lnd: Lnd)
                              (channelId: ChannelIdentifier)
                              (fundingAmount: Money) =
        async {
            let channelInfoBeforeAnyPayment = clientWallet.ChannelStore.ChannelInfo channelId
            match channelInfoBeforeAnyPayment.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let! sendHtlcPayment1Res =
                async {
                    let transferAmount =
                        let accountBalance = Money(channelInfoBeforeAnyPayment.SpendableBalance, MoneyUnit.BTC)
                        TransferAmount (walletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
                    let! invoiceOpt = 
                        lnd.CreateInvoice transferAmount None
                    let invoice = UnwrapOption invoiceOpt "Failed to create first invoice"

                    return! 
                        Lightning.Network.SendHtlcPayment
                            clientWallet.NodeClient
                            channelId
                            (PaymentInvoice.Parse invoice.BOLT11)
                            true
                }
            UnwrapResult sendHtlcPayment1Res "SendHtlcPayment failed"

            let channelInfoAfterPayment1 = clientWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let! lndBalanceAfterPayment1 = lnd.ChannelBalance()

            if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> fundingAmount - walletToWalletTestPayment1Amount then
                return failwith "incorrect balance after payment 1"
            if lndBalanceAfterPayment1 <> walletToWalletTestPayment1Amount then
                return failwith "incorrect lnd balance after payment 1"

            let! sendHtlcPayment2Res =
                async {
                    let transferAmount =
                        let accountBalance = Money(channelInfoBeforeAnyPayment.SpendableBalance, MoneyUnit.BTC)
                        TransferAmount (walletToWalletTestPayment2Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
                    let! invoiceOpt = 
                        lnd.CreateInvoice transferAmount None
                    let invoice = UnwrapOption invoiceOpt "Failed to create second invoice"

                    return! 
                        Lightning.Network.SendHtlcPayment
                            clientWallet.NodeClient
                            channelId
                            (PaymentInvoice.Parse invoice.BOLT11)
                            true
                }
            UnwrapResult sendHtlcPayment2Res "SendHtlcPayment failed"

            let channelInfoAfterPayment2 = clientWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment2.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)
            let! lndBalanceAfterPayment2 = lnd.ChannelBalance()

            if Money(channelInfoAfterPayment2.Balance, MoneyUnit.BTC) <> fundingAmount - walletToWalletTestPayment1Amount - walletToWalletTestPayment2Amount then
                return failwith "incorrect balance after payment 2"
            if lndBalanceAfterPayment2 <> lndBalanceAfterPayment1 + walletToWalletTestPayment2Amount then
                return failwith "incorrect lnd balance after payment 2"
        }

    [<Category "G2G_ChannelOpening_Funder">]
    [<Test>]
    member __.``can open channel with geewallet (funder)``() = Async.RunSynchronously <| async {
        let! _channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            OpenChannelWithFundee (Some Config.FundeeNodeEndpoint)

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Category "G2G_ChannelOpening_Fundee">]
    [<Test>]
    member __.``can open channel with geewallet (fundee)``() = Async.RunSynchronously <| async {
        let! serverWallet, _channelId = AcceptChannelFromGeewalletFunder ()

        (serverWallet :> IDisposable).Dispose()
    }

    [<Category "G2G_ChannelClosingAfterJustOpening_Funder">]
    [<Test>]
    member __.``can close channel with geewallet (funder)``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            try
                OpenChannelWithFundee (Some Config.FundeeNodeEndpoint)
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: channel-closing inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        do! ClientCloseChannel clientWallet bitcoind channelId

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Category "G2G_ChannelClosingAfterJustOpening_Fundee">]
    [<Test>]
    member __.``can close channel with geewallet (fundee)``() = Async.RunSynchronously <| async {
        let! serverWallet, channelId = AcceptChannelFromGeewalletFunder ()

        let! closeChannelRes = Lightning.Network.AcceptCloseChannel serverWallet.NodeServer channelId
        match closeChannelRes with
        | Ok _ -> ()
        | Error err -> return failwith (SPrintF1 "failed to accept close channel: %A" err)
    }

    [<Category "G2G_HtlcPayment_Funder">]
    [<Test>]
    member __.``can send htlc payments, with geewallet (funder)``() = Async.RunSynchronously <| async {
        let invoiceFileFromFundeePath =
            Path.Combine (Path.GetTempPath(), "invoice.txt")

        // Clear the invoice file from previous runs
        File.Delete (invoiceFileFromFundeePath)

        let! channelId, clientWallet, bitcoind, electrumServer, lnd, fundingAmount =
            try
                OpenChannelWithFundee (Some Config.FundeeNodeEndpoint)
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: monohop-sending inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let channelInfoBeforeAnyPayment = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! sendHtlcPayment1Res =
            async {
                let! invoiceInString =
                    let rec readInvoice (path: string) =
                        async {
                            try
                                let invoiceString = File.ReadAllText path
                                if String.IsNullOrWhiteSpace invoiceString then
                                    do! Async.Sleep 500
                                    return! readInvoice path
                                else
                                    return invoiceString
                            with
                            | :? FileNotFoundException ->
                                do! Async.Sleep 500
                                return! readInvoice path
                        }

                    readInvoice invoiceFileFromFundeePath

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoiceInString)
                        true
            }
        UnwrapResult sendHtlcPayment1Res "SendHtlcPayment failed"

        let channelInfoAfterPayment1 = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfoAfterPayment1.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> fundingAmount - walletToWalletTestPayment1Amount then
            return failwith "incorrect balance after payment 1"

        TearDown clientWallet bitcoind electrumServer lnd
    }


    [<Category "G2G_HtlcPayment_Fundee">]
    [<Test>]
    member __.``can receive htlc payments, with geewallet (fundee)``() = Async.RunSynchronously <| async {
        let! serverWallet, channelId = AcceptChannelFromGeewalletFunder ()

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let balanceBeforeAnyPayment = Money(channelInfoBeforeAnyPayment.Balance, MoneyUnit.BTC)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)
        let amountInSatoshis =
            Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
        let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"

        File.WriteAllText (Path.Combine (Path.GetTempPath(), "invoice.txt"), invoice1InString)

        let! receiveHtlcPaymentRes =
            Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId true
        let receiveHtlcPayment = UnwrapResult receiveHtlcPaymentRes "ReceiveHtlcPayment failed"
        
        match receiveHtlcPayment with
        | HtlcPayment status ->
            Assert.AreNotEqual (HTLCSettleStatus.Fail, status, "htlc payment failed gracefully")
            Assert.AreNotEqual (HTLCSettleStatus.NotSettled, status, "htlc payment didn't get settled")

            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> balanceBeforeAnyPayment + walletToWalletTestPayment1Amount then
                return failwith "incorrect balance after receiving payment 1"
        | _ ->
            Assert.Fail "received non-htlc lightning event"

        (serverWallet :> IDisposable).Dispose()
    }

    [<Category "G2G_HtlcPaymentRevocation_Funder">]
    [<Test>]
    member __.``can send htlc payments with revocation, with geewallet (funder)``() = Async.RunSynchronously <| async {
        let invoiceFileFromFundeePath =
            Path.Combine (Path.GetTempPath(), "invoice-1.txt")
        let secondInvoiceFileFromFundeePath =
            Path.Combine (Path.GetTempPath(), "invoice-2.txt")

        let rec readInvoice (path: string) =
            async {
                try
                    let invoiceString = File.ReadAllText path
                    if String.IsNullOrWhiteSpace invoiceString then
                        do! Async.Sleep 500
                        return! readInvoice path
                    else
                        return invoiceString
                with
                | :? FileNotFoundException ->
                    do! Async.Sleep 500
                    return! readInvoice path
            }

        // Clear the invoice file from previous runs
        File.Delete (invoiceFileFromFundeePath)
        File.Delete (secondInvoiceFileFromFundeePath)

        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            try
                OpenChannelWithFundee (Some Config.FundeeNodeEndpoint)
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: monohop-sending inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let channelInfoBeforeAnyPayment = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! sendHtlcPayment1Res =
            async {
                let! invoiceInString =
                    readInvoice invoiceFileFromFundeePath

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoiceInString)
                        false
            }
        UnwrapResult sendHtlcPayment1Res "SendHtlcPayment failed"

        let commitmentTx = clientWallet.ChannelStore.GetCommitmentTx channelId

        let! sendHtlcPayment2Res =
            async {
                let! invoiceInString =
                    readInvoice secondInvoiceFileFromFundeePath

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoiceInString)
                        false
            }
        UnwrapResult sendHtlcPayment2Res "SendHtlcPayment failed"

        let! _theftTxId = UtxoCoin.Account.BroadcastRawTransaction Currency.BTC (commitmentTx.ToString())

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500
        
        // Mine the force-close tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        let rec waitForClosingTx () =
            async {
                Console.WriteLine "Looking for closing tx"
                let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId clientWallet.ChannelStore
                if result then
                    return ()
                else
                    do! Async.Sleep 500
                    return! waitForClosingTx()
            }

        do! waitForClosingTx ()

        let rec waitUntilReadyForBroadcastIsNotEmpty () =
            async {
                let! _ = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId clientWallet.ChannelStore
                let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId clientWallet.ChannelStore
                if readyForBroadcast.IsDone () then
                    return readyForBroadcast
                else if readyForBroadcast.IsEmpty () then
                    Console.WriteLine "No ready for broadcast, rechecking"
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                    do! Async.Sleep 1000
                    return! waitUntilReadyForBroadcastIsNotEmpty ()
                else
                    return readyForBroadcast
            }

        let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

        let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
            async {
                if readyToBroadcastList.IsEmpty() then
                    return feeSum
                else
                    let! htlcTx, rest = (Lightning.Node.Client clientWallet.NodeClient).CreateHtlcTxForListHead readyToBroadcastHtlcTxs clientWallet.Password
                    Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))

                    do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx clientWallet.ChannelStore |> Async.Ignore
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                    return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
            }

        let! _feesPaidFor2ndStageHtlcTx = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 10u)
        do! Async.Sleep (10000)

        TearDown clientWallet bitcoind electrumServer lnd
    }


    [<Category "G2G_HtlcPaymentRevocation_Fundee">]
    [<Test>]
    member __.``can receive htlc payments with revocation, with geewallet (fundee)``() = Async.RunSynchronously <| async {
        let! serverWallet, channelId = AcceptChannelFromGeewalletFunder ()

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)
        let amountInSatoshis =
            Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
        let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"
        let invoice2InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 2"

        File.WriteAllText (Path.Combine (Path.GetTempPath(), "invoice-1.txt"), invoice1InString)
        File.WriteAllText (Path.Combine (Path.GetTempPath(), "invoice-2.txt"), invoice2InString)

        let! receiveHtlcPaymentRes =
            Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
        let receiveHtlcPayment = UnwrapResult receiveHtlcPaymentRes "ReceiveHtlcPayment failed"
        
        match receiveHtlcPayment with
        | HtlcPayment _status ->
            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let! receiveHtlcPaymentRes =
                Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
            let receiveHtlcPayment = UnwrapResult receiveHtlcPaymentRes "ReceiveHtlcPayment failed"
            match receiveHtlcPayment with
            | HtlcPayment _status ->
                try
                    let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
                    match channelInfoAfterPayment1.Status with
                    | ChannelStatus.Active -> ()
                    | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

                    let! balanceBeforeFundsReclaimed = serverWallet.GetBalance()

                    let rec waitForClosingTx () =
                        async {
                            Console.WriteLine "Looking for closing tx"
                            let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                            if result then
                                return ()
                            else
                                do! Async.Sleep 500
                                return! waitForClosingTx()
                        }

                    do! waitForClosingTx ()

                    let rec waitUntilReadyForBroadcastIsNotEmpty () =
                        async {
                            let! _result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                            let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId serverWallet.ChannelStore
                            if readyForBroadcast.IsDone () then
                                return readyForBroadcast
                            else if readyForBroadcast.IsEmpty () then
                                Console.WriteLine "No ready for broadcast, rechecking"
                                do! Async.Sleep 100
                                return! waitUntilReadyForBroadcastIsNotEmpty ()
                            else
                                return readyForBroadcast
                        }

                    let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

                    let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
                        async {
                            if readyToBroadcastList.IsEmpty() then
                                return feeSum
                            else
                                let! htlcTx, rest = (Lightning.Node.Server serverWallet.NodeServer).CreateHtlcTxForListHead readyToBroadcastHtlcTxs serverWallet.Password
                                Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))
                                do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx serverWallet.ChannelStore |> Async.Ignore

                                return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
                        }

                    let! feesPaid = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

                    do! serverWallet.WaitForBalance (balanceBeforeFundsReclaimed + walletToWalletTestPayment1Amount - feesPaid) |> Async.Ignore
                with
                | ex -> Console.WriteLine (ex.ToString())
            | _ ->
                Assert.Fail "received non-htlc lightning event"
        | _ ->
            Assert.Fail "received non-htlc lightning event"
            
        (serverWallet :> IDisposable).Dispose()
    }

    [<Category "G2G_HtlcPaymentRevocation2_Funder">]
    [<Test>]
    member __.``can send htlc payments with revocation 2, with geewallet (funder)``() = Async.RunSynchronously <| async {
        let invoiceFileFromFundeePath =
            Path.Combine (Path.GetTempPath(), "invoice-1.txt")
        let secondInvoiceFileFromFundeePath =
            Path.Combine (Path.GetTempPath(), "invoice-2.txt")
        let fundeeWalletAddressPath =
            Path.Combine (Path.GetTempPath(), "address.txt")

        let rec readInvoice (path: string) =
            async {
                try
                    let invoiceString = File.ReadAllText path
                    if String.IsNullOrWhiteSpace invoiceString then
                        do! Async.Sleep 500
                        return! readInvoice path
                    else
                        return invoiceString
                with
                | :? FileNotFoundException ->
                    do! Async.Sleep 500
                    return! readInvoice path
            }

        // Clear the invoice file from previous runs
        File.Delete (invoiceFileFromFundeePath)
        File.Delete (secondInvoiceFileFromFundeePath)
        File.Delete (fundeeWalletAddressPath)

        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            try
                OpenChannelWithFundee (Some Config.FundeeNodeEndpoint)
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: monohop-sending inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let channelInfoBeforeAnyPayment = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! fundeeWalletAddress =
            readInvoice fundeeWalletAddressPath

        // fund geewallet
        let geewalletAccountAmount = Money (10m, MoneyUnit.BTC)
        let! feeRate = ElectrumServer.EstimateFeeRate()
        let! _txid = lnd.SendCoins geewalletAccountAmount (BitcoinScriptAddress(fundeeWalletAddress, Network.RegTest)) feeRate

        // wait for lnd's transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            Thread.Sleep 500
        
        // We want to make sure Geewallet considers the money received.
        // A typical number of blocks that is almost universally considered
        // 100% confirmed, is 6. Therefore we mine 7 blocks. Because we have
        // waited for the transaction to appear in bitcoind's mempool, we
        // can assume that the first of the 7 blocks will include the
        // transaction sending money to Geewallet. The next 6 blocks will
        // bury the first block, so that the block containing the transaction
        // will be 6 deep at the end of the following call to generateBlocks.
        // At that point, the 0.25 regtest coins from the above call to sendcoins
        // are considered arrived to Geewallet.
        let consideredConfirmedAmountOfBlocksPlusOne = BlockHeightOffset32 7u
        bitcoind.GenerateBlocksToDummyAddress consideredConfirmedAmountOfBlocksPlusOne
        
        let! sendHtlcPayment1Res =
            async {
                let! invoiceInString =
                    readInvoice invoiceFileFromFundeePath

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoiceInString)
                        false
            }
        UnwrapResult sendHtlcPayment1Res "SendHtlcPayment failed"

        let! sendHtlcPayment2Res =
            async {
                let! invoiceInString =
                    readInvoice secondInvoiceFileFromFundeePath

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoiceInString)
                        false
            }
        UnwrapResult sendHtlcPayment2Res "SendHtlcPayment failed"

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500
        
        // Mine the force-close tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        let! balanceBeforeRevocation = clientWallet.GetBalance()

        let rec waitForClosingTx () =
            async {
                Console.WriteLine "Looking for closing tx"
                let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId clientWallet.ChannelStore
                if result then
                    return ()
                else
                    do! Async.Sleep 500
                    return! waitForClosingTx()
            }

        do! waitForClosingTx ()

        let rec waitUntilReadyForBroadcastIsNotEmpty () =
            async {
                let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId clientWallet.ChannelStore
                if readyForBroadcast.IsDone () then
                    return readyForBroadcast
                else if readyForBroadcast.IsEmpty () then
                    Console.WriteLine "No ready for broadcast, rechecking"
                    do! Async.Sleep 1000
                    return! waitUntilReadyForBroadcastIsNotEmpty ()
                else
                    return readyForBroadcast
            }

        let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()
        
        //Wait for fundee to broadcast success tx
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500
        
        // Mine the 2nd stage tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
            async {
                if readyToBroadcastList.IsEmpty() then
                    return feeSum
                else
                    let! htlcTx, rest = (Lightning.Node.Client clientWallet.NodeClient).CreateHtlcTxForListHead readyToBroadcastHtlcTxs clientWallet.Password
                    Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))

                    do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx clientWallet.ChannelStore |> Async.Ignore
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                    return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
            }

        let! feesPaidFor2ndStageHtlcTx = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

        do! clientWallet.WaitForBalance (balanceBeforeRevocation + walletToWalletTestPayment1Amount - feesPaidFor2ndStageHtlcTx) |> Async.Ignore

        TearDown clientWallet bitcoind electrumServer lnd
    }


    [<Category "G2G_HtlcPaymentRevocation2_Fundee">]
    [<Test>]
    member __.``can receive htlc payments with revocation2, with geewallet (fundee)``() = Async.RunSynchronously <| async {
        let! serverWallet, channelId = AcceptChannelFromGeewalletFunder ()

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)
        let amountInSatoshis =
            Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
        let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"
        let invoice2InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 2"

        File.WriteAllText (Path.Combine (Path.GetTempPath(), "invoice-1.txt"), invoice1InString)
        File.WriteAllText (Path.Combine (Path.GetTempPath(), "invoice-2.txt"), invoice2InString)
        File.WriteAllText (Path.Combine (Path.GetTempPath(), "address.txt"), serverWallet.Address.ToString())

        let! receiveHtlcPaymentRes =
            Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
        let receiveHtlcPayment = UnwrapResult receiveHtlcPaymentRes "ReceiveHtlcPayment failed"
        
        match receiveHtlcPayment with
        | HtlcPayment _status ->
            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let commitmentTx = serverWallet.ChannelStore.GetCommitmentTx channelId

            let! receiveHtlcPaymentRes =
                Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
            let receiveHtlcPayment = UnwrapResult receiveHtlcPaymentRes "ReceiveHtlcPayment failed"
            match receiveHtlcPayment with
            | HtlcPayment _status ->
                let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
                match channelInfoAfterPayment1.Status with
                | ChannelStatus.Active -> ()
                | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

                let! _theftTxId = UtxoCoin.Account.BroadcastRawTransaction Currency.BTC (commitmentTx.ToString())

                let rec waitForClosingTx () =
                    async {
                        Console.WriteLine "Looking for closing tx"
                        let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                        if result then
                            return ()
                        else
                            do! Async.Sleep 500
                            return! waitForClosingTx()
                    }

                do! waitForClosingTx ()

                let rec waitUntilReadyForBroadcastIsNotEmpty () =
                    async {
                        let! _result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                        let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId serverWallet.ChannelStore
                        if readyForBroadcast.IsDone () then
                            return readyForBroadcast
                        else if readyForBroadcast.IsEmpty () then
                            Console.WriteLine "No ready for broadcast, rechecking"
                            do! Async.Sleep 100
                            return! waitUntilReadyForBroadcastIsNotEmpty ()
                        else
                            return readyForBroadcast
                    }

                let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

                let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
                    async {
                        if readyToBroadcastList.IsEmpty() then
                            return feeSum
                        else
                            let! htlcTx, rest = (Lightning.Node.Server serverWallet.NodeServer).CreateHtlcTxForListHead readyToBroadcastHtlcTxs serverWallet.Password
                            Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))
                            do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx serverWallet.ChannelStore |> Async.Ignore

                            return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
                    }

                let! _feesPaid = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

                ()
            | _ ->
                Assert.Fail "received non-htlc lightning event"
        | _ ->
            Assert.Fail "received non-htlc lightning event"
            
        (serverWallet :> IDisposable).Dispose()
    }

    [<Test>]
    member __.``can open channel with LND``() = Async.RunSynchronously <| async {
        let! _channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount = OpenChannelWithFundee None

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Test>]
    member __.``can open channel with LND and send htlcs``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, fundingAmount = OpenChannelWithFundee None

        do! SendHtlcPaymentsToLnd clientWallet lnd channelId fundingAmount

        TearDown clientWallet bitcoind electrumServer lnd
    }
    
    [<Test>]
    member __.``can open channel with LND and send invalid htlc``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, fundingAmount = OpenChannelWithFundee None

        let channelInfoBeforeAnyPayment = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! sendHtlcPayment1Res =
            async {
                let transferAmount =
                    let accountBalance = Money(channelInfoBeforeAnyPayment.SpendableBalance, MoneyUnit.BTC)
                    TransferAmount (walletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
                let! invoiceOpt = 
                    lnd.CreateInvoice transferAmount (TimeSpan.FromSeconds 1. |> Some)
                let invoice = UnwrapOption invoiceOpt "Failed to create first invoice"

                do! Async.Sleep 2000

                return! 
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoice.BOLT11)
                        true
            }
        match sendHtlcPayment1Res with
        | Error _err ->
            let channelInfoAfterPayment1 = clientWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let! lndBalanceAfterPayment1 = lnd.ChannelBalance()

            if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> fundingAmount then
                return failwith "incorrect balance after failed payment 1"
            if lndBalanceAfterPayment1 <> Money.Zero then
                return failwith "incorrect lnd balance after failed payment 1"
        | Ok _ ->
            return failwith "SendHtlcPayment returtned ok"
        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Category "HtlcOnChainEnforce">]
    [<Test>]
    member __.``can open channel with LND and send invalid htlc but settle on-chain (force close initiated by lnd)``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount = OpenChannelWithFundee None

        let channelInfo = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! _sendHtlcPayment1Res =
            async {
                let transferAmount =
                    let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                    TransferAmount (walletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
                let! invoiceOpt =
                    lnd.CreateInvoice transferAmount (TimeSpan.FromSeconds 1. |> Some)
                let invoice = UnwrapOption invoiceOpt "Failed to create first invoice"

                do! Async.Sleep 2000

                return!
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoice.BOLT11)
                        false
            }

        let fundingOutPoint =
            let fundingTxId = uint256(channelInfo.FundingTxId.ToString())
            let fundingOutPointIndex = channelInfo.FundingOutPointIndex
            OutPoint(fundingTxId, fundingOutPointIndex)

        // We use `Async.Start` because close channel api doesn't return until close/sweep process is finished
        lnd.CloseChannel fundingOutPoint true |> Async.Start

        do! Async.Sleep 5000

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500

        let! balanceBeforeFundsReclaimed = clientWallet.GetBalance()

        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        let rec waitForClosingTx () =
            async {
                let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId clientWallet.ChannelStore
                if result then
                    return ()
                else
                    do! Async.Sleep 500
                    return! waitForClosingTx()
            }

        do! waitForClosingTx ()

        let rec waitUntilReadyForBroadcastIsNotEmpty () =
            async {
                let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId clientWallet.ChannelStore
                if readyForBroadcast.IsEmpty () then
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                    do! Async.Sleep 100
                    return! waitUntilReadyForBroadcastIsNotEmpty ()
                else
                    return readyForBroadcast
            }

        let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

        let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
            async {
                if readyToBroadcastList.IsEmpty() then
                    return feeSum
                else
                    let! htlcTx, rest = (Lightning.Node.Client clientWallet.NodeClient).CreateHtlcTxForListHead readyToBroadcastHtlcTxs clientWallet.Password
                    do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx clientWallet.ChannelStore |> Async.Ignore

                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                    return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
            }

        let! feesPaid = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

        do! clientWallet.WaitForBalance (balanceBeforeFundsReclaimed + walletToWalletTestPayment1Amount - feesPaid) |> Async.Ignore

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Test>]
    member __.``can accept channel from LND and receive htlcs``() = Async.RunSynchronously <| async {
        let! channelId, serverWallet, bitcoind, electrumServer, lnd = AcceptChannelFromLndFunder ()

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let balanceBeforeAnyPayment = Money(channelInfoBeforeAnyPayment.Balance, MoneyUnit.BTC)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)

        let sendLndPayment1Job = async {
            // Wait for lnd to recognize we're online
            do! Async.Sleep 10000

            let amountInSatoshis =
                Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
            let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"

            do! lnd.SendPayment invoice1InString
        }
        let receiveGeewalletPayment = async {
            let! receiveMonoHopPaymentRes =
                Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId true
            return UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"
        }

        let! (_, receiveLightningEventResult) = AsyncExtensions.MixedParallel2 sendLndPayment1Job receiveGeewalletPayment

        match receiveLightningEventResult with
        | IncomingChannelEvent.HtlcPayment status ->
            Assert.AreNotEqual (HTLCSettleStatus.Fail, status, "htlc payment failed gracefully")
            Assert.AreNotEqual (HTLCSettleStatus.NotSettled, status, "htlc payment didn't get settled which shouldn't happen because ReceiveLightningEvent's settleHTLCImmediately should be true")

            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> balanceBeforeAnyPayment + walletToWalletTestPayment1Amount then
                return failwith "incorrect balance after receiving payment 1"
        | _ ->
            Assert.Fail "received non-htlc lightning event"

        TearDown serverWallet bitcoind electrumServer lnd
    }

    [<Category "HtlcOnChainEnforce">]
    [<Test>]
    member __.``can accept channel from LND and receive htlcs but settle on-chain (force close initiated by lnd)``() = Async.RunSynchronously <| async {
        let! channelId, serverWallet, bitcoind, electrumServer, lnd = AcceptChannelFromLndFunder ()

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)

        let sendLndPayment1Job = async {
            // Wait for lnd to recognize we're online
            do! Async.Sleep 10000

            let amountInSatoshis =
                Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
            let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"

            // We use `Async.Start` because send payment api doesn't return until payment is settled (which doesn't happen immediately in this test)
            lnd.SendPayment invoice1InString |> Async.Start
        }
        let receiveGeewalletPayment = async {
            let! receiveMonoHopPaymentRes =
                Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
            return UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"
        }

        let! (_, receiveLightningEventResult) = AsyncExtensions.MixedParallel2 sendLndPayment1Job receiveGeewalletPayment

        match receiveLightningEventResult with
        | IncomingChannelEvent.HtlcPayment status ->
            Assert.AreEqual (HTLCSettleStatus.NotSettled, status, "htlc payment got settled")

            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let fundingOutPoint =
                let fundingTxId = uint256(channelInfoAfterPayment1.FundingTxId.ToString())
                let fundingOutPointIndex = channelInfoAfterPayment1.FundingOutPointIndex
                OutPoint(fundingTxId, fundingOutPointIndex)

            // We use `Async.Start` because close channel api doesn't return until close/sweep process is finished
            lnd.CloseChannel fundingOutPoint true |> Async.Start

            do! Async.Sleep 5000

            // wait for force-close transaction to appear in mempool
            while bitcoind.GetTxIdsInMempool().Length = 0 do
                do! Async.Sleep 500

            let! balanceBeforeFundsReclaimed = serverWallet.GetBalance()

            bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

            let rec waitForClosingTx () =
                async {
                    let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                    if result then
                        return ()
                    else
                        do! Async.Sleep 500
                        return! waitForClosingTx()
                }

            do! waitForClosingTx ()

            let rec waitUntilReadyForBroadcastIsNotEmpty () =
                async {
                    let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId serverWallet.ChannelStore
                    if readyForBroadcast.IsEmpty () then
                        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                        do! Async.Sleep 100
                        return! waitUntilReadyForBroadcastIsNotEmpty ()
                    else
                        return readyForBroadcast
                }

            let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

            let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
                async {
                    if readyToBroadcastList.IsEmpty() then
                        return feeSum
                    else
                        let! htlcTx, rest = (Lightning.Node.Server serverWallet.NodeServer).CreateHtlcTxForListHead readyToBroadcastHtlcTxs serverWallet.Password
                        do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx serverWallet.ChannelStore |> Async.Ignore
                        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                        return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
                }

            let! feesPaid = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

            do! serverWallet.WaitForBalance (balanceBeforeFundsReclaimed + walletToWalletTestPayment1Amount - feesPaid) |> Async.Ignore

        | _ ->
            Assert.Fail "received non-htlc lightning event"

        TearDown serverWallet bitcoind electrumServer lnd
    }
    [<Category "HtlcOnChainEnforce">]
    [<Test>]
    member __.``can accept channel from LND and receive htlcs but settle on-chain (force close initiated by geewallet)``() = Async.RunSynchronously <| async {
        let! channelId, serverWallet, bitcoind, electrumServer, lnd = AcceptChannelFromLndFunder ()

        do! serverWallet.FundByMining bitcoind lnd

        let channelInfoBeforeAnyPayment = serverWallet.ChannelStore.ChannelInfo channelId
        match channelInfoBeforeAnyPayment.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let invoiceManager = InvoiceManagement (serverWallet.Account :?> NormalUtxoAccount, serverWallet.Password)

        let sendLndPayment1Job = async {
            // Wait for lnd to recognize we're online
            do! Async.Sleep 10000

            let amountInSatoshis =
                Convert.ToUInt64 walletToWalletTestPayment1Amount.Satoshi
            let invoice1InString = invoiceManager.CreateInvoice amountInSatoshis "Payment 1"

            // We use `Async.Start` because send payment api doesn't return until payment is settled (which doesn't happen immediately in this test)
            lnd.SendPayment invoice1InString |> Async.Start
        }
        let receiveGeewalletPayment = async {
            let! receiveMonoHopPaymentRes =
                Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId false
            return UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"
        }

        let! (_, receiveLightningEventResult) = AsyncExtensions.MixedParallel2 sendLndPayment1Job receiveGeewalletPayment

        match receiveLightningEventResult with
        | IncomingChannelEvent.HtlcPayment status ->
            Assert.AreEqual (HTLCSettleStatus.NotSettled, status, "htlc payment got settled")

            let channelInfoAfterPayment1 = serverWallet.ChannelStore.ChannelInfo channelId
            match channelInfoAfterPayment1.Status with
            | ChannelStatus.Active -> ()
            | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

            let! _forceCloseTxId = (Lightning.Node.Server serverWallet.NodeServer).ForceCloseChannel channelId
            
            let locallyForceClosedData =
                match (serverWallet.ChannelStore.ChannelInfo channelId).Status with
                | ChannelStatus.LocallyForceClosed locallyForceClosedData ->
                    locallyForceClosedData
                | status -> failwith (SPrintF1 "unexpected channel status. Expected LocallyForceClosed, got %A" status)

            // wait for force-close transaction to appear in mempool
            while bitcoind.GetTxIdsInMempool().Length = 0 do
                do! Async.Sleep 500
            
            // Mine the force-close tx into a block
            bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
            
            let! balanceBeforeFundsReclaimed = serverWallet.GetBalance()

            let rec waitForClosingTx () =
                async {
                    Console.WriteLine "Looking for closing tx"
                    let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId serverWallet.ChannelStore
                    if result then
                        return ()
                    else
                        do! Async.Sleep 500
                        return! waitForClosingTx()
                }

            do! waitForClosingTx ()

            let rec waitUntilReadyForBroadcastIsNotEmpty () =
                async {
                    let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId serverWallet.ChannelStore
                    if readyForBroadcast.IsEmpty () then
                        Console.WriteLine "No ready for broadcast, rechecking"
                        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                        do! Async.Sleep 100
                        return! waitUntilReadyForBroadcastIsNotEmpty ()
                    else
                        return readyForBroadcast
                }

            let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

            let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
                async {
                    if readyToBroadcastList.IsEmpty() then
                        return feeSum
                    else
                        let! htlcTx, rest = (Lightning.Node.Server serverWallet.NodeServer).CreateHtlcTxForListHead readyToBroadcastHtlcTxs serverWallet.Password
                        Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))

                        do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx serverWallet.ChannelStore |> Async.Ignore
                        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                        return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
                }

            let! feesPaidFor2ndStageHtlcTx = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

            bitcoind.GenerateBlocksToDummyAddress (locallyForceClosedData.ToSelfDelay |> uint32 |> BlockHeightOffset32)

            let rec checkForReadyToSpend2ndStageClaim ()  =
                async {
                    let! readyForBroadcast = ChainWatcher.CheckForReadyToSpendDelayedHtlcTransactions channelId serverWallet.ChannelStore
                    if List.isEmpty readyForBroadcast then
                        Console.WriteLine "No ready for spend, rechecking"
                        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                        do! Async.Sleep 100
                        return! checkForReadyToSpend2ndStageClaim ()
                    else
                        return readyForBroadcast
                    
                }

            let! readyToSpend2ndStages = checkForReadyToSpend2ndStageClaim()

            let rec spend2ndStages (readyToSpend2ndStages: List<AmountInSatoshis * TransactionIdentifier>)  =
                async {
                    let! recoveryTxs = (Lightning.Node.Server serverWallet.NodeServer).CreateRecoveryTxForDelayedHtlcTx channelId readyToSpend2ndStages
                    Console.WriteLine (sprintf "Broadcasting... %A" recoveryTxs)
                    let rec broadcastSpendingTxs (recoveryTxs: List<HtlcRecoveryTx>) (feeSum: Money) =
                        async {
                            match recoveryTxs with
                            | [] -> return feeSum
                            | recoveryTx::rest ->
                                do! ChannelManager.BroadcastHtlcRecoveryTxAndRemoveFromWatchList recoveryTx serverWallet.ChannelStore |> Async.Ignore
                                bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                                return! broadcastSpendingTxs rest (feeSum + (Money.Satoshis recoveryTx.Fee.EstimatedFeeInSatoshis))
                        }
                    return! broadcastSpendingTxs recoveryTxs Money.Zero
                }

            let! feePaidForClaiming2ndtSage = spend2ndStages readyToSpend2ndStages

            do! serverWallet.WaitForBalance (balanceBeforeFundsReclaimed + walletToWalletTestPayment1Amount - feesPaidFor2ndStageHtlcTx - feePaidForClaiming2ndtSage) |> Async.Ignore

        | _ ->
            Assert.Fail "received non-htlc lightning event"

        TearDown serverWallet bitcoind electrumServer lnd
    }

    [<Test>]
    [<Category "HtlcOnChainEnforce">]
    member __.``can accept channel from LND and send invalid htlc but settle on-chain (force close initiated by geewallet)``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount = OpenChannelWithFundee None

        let channelInfo = clientWallet.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! _sendHtlcPayment1Res =
            async {
                let transferAmount =
                    let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                    TransferAmount (walletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
                let! invoiceOpt =
                    lnd.CreateInvoice transferAmount (TimeSpan.FromSeconds 1. |> Some)
                let invoice = UnwrapOption invoiceOpt "Failed to create first invoice"

                do! Async.Sleep 2000

                return!
                    Lightning.Network.SendHtlcPayment
                        clientWallet.NodeClient
                        channelId
                        (PaymentInvoice.Parse invoice.BOLT11)
                        false
            }

        let! _forceCloseTxId = (Lightning.Node.Client clientWallet.NodeClient).ForceCloseChannel channelId

        let locallyForceClosedData =
            match (clientWallet.ChannelStore.ChannelInfo channelId).Status with
            | ChannelStatus.LocallyForceClosed locallyForceClosedData ->
                locallyForceClosedData
            | status -> failwith (SPrintF1 "unexpected channel status. Expected LocallyForceClosed, got %A" status)

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500

        Infrastructure.LogDebug (SPrintF1 "the time lock is %i blocks" locallyForceClosedData.ToSelfDelay)

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500

        // Mine the force-close tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        let! balanceBeforeFundsReclaimed = clientWallet.GetBalance()

        let rec waitForClosingTx () =
            async {
                Console.WriteLine "Looking for closing tx"
                let! result = ChainWatcher.CheckForChannelForceCloseAndSaveUnresolvedHtlcs channelId clientWallet.ChannelStore
                if result then
                    return ()
                else
                    do! Async.Sleep 500
                    return! waitForClosingTx()
            }

        do! waitForClosingTx ()

        let rec waitUntilReadyForBroadcastIsNotEmpty () =
            async {
                let! readyForBroadcast = ChainWatcher.CheckForChannelReadyToBroadcastHtlcTransactions channelId clientWallet.ChannelStore
                if readyForBroadcast.IsEmpty () then
                    Console.WriteLine "No ready for broadcast, rechecking"
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                    do! Async.Sleep 100
                    return! waitUntilReadyForBroadcastIsNotEmpty ()
                else
                    return readyForBroadcast
            }

        let! readyToBroadcastHtlcTxs = waitUntilReadyForBroadcastIsNotEmpty()

        let rec broadcastUntilListIsEmpty (readyToBroadcastList: HtlcTxsList) (feeSum: Money) =
            async {
                if readyToBroadcastList.IsEmpty() then
                    return feeSum
                else
                    let! htlcTx, rest = (Lightning.Node.Client clientWallet.NodeClient).CreateHtlcTxForListHead readyToBroadcastHtlcTxs clientWallet.Password
                    Console.WriteLine (sprintf "Broadcasting... %s" (htlcTx.Tx.ToString()))

                    do! ChannelManager.BroadcastHtlcTxAndAddToWatchList htlcTx clientWallet.ChannelStore |> Async.Ignore
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                    return! broadcastUntilListIsEmpty rest (feeSum + (Money.Satoshis htlcTx.Fee.EstimatedFeeInSatoshis))
            }

        let! feesPaidFor2ndStageHtlcTx = broadcastUntilListIsEmpty readyToBroadcastHtlcTxs Money.Zero

        bitcoind.GenerateBlocksToDummyAddress (locallyForceClosedData.ToSelfDelay |> uint32 |> BlockHeightOffset32)

        let rec checkForReadyToSpend2ndStageClaim ()  =
            async {
                let! readyForBroadcast = ChainWatcher.CheckForReadyToSpendDelayedHtlcTransactions channelId clientWallet.ChannelStore
                if List.isEmpty readyForBroadcast then
                    Console.WriteLine "No ready for spend, rechecking"
                    bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
                    do! Async.Sleep 100
                    return! checkForReadyToSpend2ndStageClaim ()
                else
                    return readyForBroadcast
                
            }

        let! readyToSpend2ndStages = checkForReadyToSpend2ndStageClaim()

        let rec spend2ndStages (readyToSpend2ndStages: List<AmountInSatoshis * TransactionIdentifier>)  =
            async {
                let! recoveryTxs = (Lightning.Node.Client clientWallet.NodeClient).CreateRecoveryTxForDelayedHtlcTx channelId readyToSpend2ndStages
                Console.WriteLine (sprintf "Broadcasting... %A" recoveryTxs)
                let rec broadcastSpendingTxs (recoveryTxs: List<HtlcRecoveryTx>) (feeSum: Money) =
                    async {
                        match recoveryTxs with
                        | [] -> return feeSum
                        | recoveryTx::rest ->
                            do! ChannelManager.BroadcastHtlcRecoveryTxAndRemoveFromWatchList recoveryTx clientWallet.ChannelStore |> Async.Ignore
                            bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

                            return! broadcastSpendingTxs rest (feeSum + (Money.Satoshis recoveryTx.Fee.EstimatedFeeInSatoshis))
                    }
                return! broadcastSpendingTxs recoveryTxs Money.Zero
            }

        let! feePaidForClaiming2ndtSage = spend2ndStages readyToSpend2ndStages

        do! clientWallet.WaitForBalance (balanceBeforeFundsReclaimed + walletToWalletTestPayment1Amount - feesPaidFor2ndStageHtlcTx - feePaidForClaiming2ndtSage) |> Async.Ignore

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Test>]
    member __.``can close channel with LND``() = Async.RunSynchronously <| async {

        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            try
                OpenChannelWithFundee None
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: channel-closing inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let! closeChannelRes = Lightning.Network.CloseChannel clientWallet.NodeClient channelId None
        match closeChannelRes with
        | Ok _ -> ()
        | Error err -> return failwith (SPrintF1 "error when closing channel: %s" (err :> IErrorMsg).Message)

        match (clientWallet.ChannelStore.ChannelInfo channelId).Status with
        | ChannelStatus.Closing -> ()
        | status -> return failwith (SPrintF1 "unexpected channel status. Expected Closing, got %A" status)

        // Mine 10 blocks to make sure closing tx is confirmed
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 (uint32 10))

        let rec waitForClosingTxConfirmed attempt = async {
            Infrastructure.LogDebug (SPrintF1 "Checking if closing tx is finished, attempt #%d" attempt)
            if attempt = 10 then
                return Error "Closing tx not confirmed after maximum attempts"
            else
                let! closingTxResult = Lightning.Network.CheckClosingFinished clientWallet.ChannelStore channelId
                match closingTxResult with
                | Tx (Full, _closingTx) ->
                    return Ok ()
                | _ ->
                    do! Async.Sleep 1000
                    return! waitForClosingTxConfirmed (attempt + 1)
        }

        let! closingTxConfirmedRes = waitForClosingTxConfirmed 0
        match closingTxConfirmedRes with
        | Ok _ -> ()
        | Error err -> return failwith (SPrintF1 "error when waiting for closing tx to confirm: %s" err)

        TearDown clientWallet bitcoind electrumServer lnd
    }

    [<Test>]
    member __.``can accept channel from LND``() = Async.RunSynchronously <| async {
        let! _channelId, serverWallet, bitcoind, electrumServer, lnd = AcceptChannelFromLndFunder ()

        TearDown serverWallet bitcoind electrumServer lnd
    }

    [<Test>]
    member __.``can accept channel closure from LND``() = Async.RunSynchronously <| async {
        let! channelId, serverWallet, bitcoind, electrumServer, lnd =
            try
                AcceptChannelFromLndFunder ()
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: channel-closing inconclusive because Channel accept failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let channelInfo = serverWallet.ChannelStore.ChannelInfo channelId

        // wait for lnd to realise we're offline
        do! Async.Sleep 1000
        let fundingOutPoint =
            let fundingTxId = uint256(channelInfo.FundingTxId.ToString())
            let fundingOutPointIndex = channelInfo.FundingOutPointIndex
            OutPoint(fundingTxId, fundingOutPointIndex)
        let closeChannelTask = async {
            match serverWallet.NodeEndPoint with
            | EndPointType.Tcp endPoint ->
                do! lnd.ConnectTo endPoint
                do! Async.Sleep 1000
                do! lnd.CloseChannel fundingOutPoint false
                return ()
            | EndPointType.Tor _torEndPoint ->
                failwith "this should be a nonexistent case as all LND tests are done using TCP at the moment and TCP connections will always have a NodeEndPoint"
        }
        let awaitCloseTask = async {
            let rec receiveEvent () = async {
                let! receivedEvent = Lightning.Network.ReceiveLightningEvent serverWallet.NodeServer channelId true
                match receivedEvent with
                | Error err ->
                    return Error (SPrintF1 "Failed to receive shutdown msg from LND: %A" err)
                | Ok event when event = IncomingChannelEvent.Shutdown ->
                    return Ok ()
                | _ -> return! receiveEvent ()
            }

            let! receiveEventRes = receiveEvent()
            UnwrapResult receiveEventRes "failed to accept close channel"

            // Wait for the closing transaction to appear in mempool
            while bitcoind.GetTxIdsInMempool().Length = 0 do
                Thread.Sleep 500

            // Mine blocks on top of the closing transaction to make it confirmed.
            let minimumDepth = BlockHeightOffset32 6u
            bitcoind.GenerateBlocksToDummyAddress minimumDepth
            return ()
        }

        let! (), () = AsyncExtensions.MixedParallel2 closeChannelTask awaitCloseTask

        TearDown serverWallet bitcoind electrumServer lnd

        return ()
    }

    [<Test>]
    member __.``can force-close channel with lnd``() = Async.RunSynchronously <| async {
        let! channelId, clientWallet, bitcoind, electrumServer, lnd, _fundingAmount =
            try
                OpenChannelWithFundee None
            with
            | ex ->
                Assert.Fail (
                    sprintf
                        "Inconclusive: channel-closing inconclusive because Channel open failed, fix this first: %s"
                        (ex.ToString())
                )
                failwith "unreachable"

        let! _forceCloseTxId = (Lightning.Node.Client clientWallet.NodeClient).ForceCloseChannel channelId

        let locallyForceClosedData =
            match (clientWallet.ChannelStore.ChannelInfo channelId).Status with
            | ChannelStatus.LocallyForceClosed locallyForceClosedData ->
                locallyForceClosedData
            | status -> failwith (SPrintF1 "unexpected channel status. Expected LocallyForceClosed, got %A" status)

        // wait for force-close transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500

        Infrastructure.LogDebug (SPrintF1 "the time lock is %i blocks" locallyForceClosedData.ToSelfDelay)

        let! balanceBeforeFundsReclaimed = clientWallet.GetBalance()

        // Mine the force-close tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        // Mine blocks to release time-lock
        bitcoind.GenerateBlocksToDummyAddress
            (BlockHeightOffset32 (uint32 locallyForceClosedData.ToSelfDelay))

        let! spendingTxResult =
            let commitmentTx = clientWallet.ChannelStore.GetCommitmentTx channelId
            (Lightning.Node.Client clientWallet.NodeClient).CreateRecoveryTxForForceClose
                channelId
                commitmentTx

        let recoveryTx = UnwrapResult spendingTxResult "Local output is dust, recovery tx cannot be created"

        let! _recoveryTxId =
            ChannelManager.BroadcastRecoveryTxAndCloseChannel recoveryTx clientWallet.ChannelStore

        // wait for spending transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500

        // Mine the spending tx into a block
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)

        Infrastructure.LogDebug "waiting for our wallet balance to increase"
        let! _balanceAfterFundsReclaimed =
            let amount = balanceBeforeFundsReclaimed + Money(1.0m, MoneyUnit.Satoshi)
            clientWallet.WaitForBalance amount

        TearDown clientWallet bitcoind electrumServer lnd
    }
