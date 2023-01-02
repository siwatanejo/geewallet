﻿namespace GWallet.Backend.Tests.End2End

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open DotNetLightning.Utils
open ResultUtils.Portability
open NBitcoin

open GWallet.Backend
open GWallet.Backend.UtxoCoin.Lightning
open GWallet.Backend.FSharpUtil.UwpHacks

type Lnd = {
    LndDir: string
    ProcessWrapper: ProcessWrapper
    ConnectionString: string
    ClientFactory: ILightningClientFactory
} with
    interface IDisposable with
        member self.Dispose() =
            Infrastructure.LogDebug "About to kill LND process..."
            self.ProcessWrapper.Process.Kill()
            self.ProcessWrapper.WaitForExit()
            Directory.Delete(self.LndDir, true)

    static member Start(bitcoind: Bitcoind): Async<Lnd> = async {
        let lndDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory lndDir |> ignore
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let processWrapper =
            let args =
                ""
                + " --bitcoin.active"
                + " --bitcoin.regtest"
                + " --bitcoin.node=bitcoind"
                + " --bitcoind.dir=" + bitcoind.DataDir
(* not needed anymore:
                + " --bitcoind.rpcuser=" + bitcoind.RpcUser
                + " --bitcoind.rpcpass=" + bitcoind.RpcPassword
                + " --bitcoind.zmqpubrawblock=tcp://127.0.0.1:28332"
                + " --bitcoind.zmqpubrawtx=tcp://127.0.0.1:28333"
*)
                + " --bitcoind.rpchost=localhost:18554"
                + " --debuglevel=trace"
                + " --listen=127.0.0.2"
                + " --restlisten=127.0.0.2:8080"
                + " --lnddir=" + lndDir
            ProcessWrapper.New
                "lnd"
                args
                Map.empty
                false
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "gRPC proxy started at 127.0.0.2:8080")
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "Waiting for wallet encryption password. Use `lncli create` to create a wallet, `lncli unlock` to unlock an existing wallet, or `lncli changepassword` to change the password of an existing wallet and unlock it.")
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let connectionString =
            ""
            + "type=lnd-rest;"
            + "server=https://127.0.0.2:8080;"
            + "allowinsecure=true;"
            + "macaroonfilepath=" + Path.Combine(lndDir, "data/chain/bitcoin/regtest/admin.macaroon")
        let clientFactory = new LightningClientFactory(NBitcoin.Network.RegTest) :> ILightningClientFactory
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let lndClient = clientFactory.Create connectionString :?> LndClient
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let walletPassword = Path.GetRandomFileName()
        let! genSeedResp = Async.AwaitTask <| lndClient.SwaggerClient.GenSeedAsync(null, null)
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let initWalletReq =
            LnrpcInitWalletRequest (
                Wallet_password = Encoding.ASCII.GetBytes walletPassword,
                Cipher_seed_mnemonic = genSeedResp.Cipher_seed_mnemonic
            )
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)

        let! _ = Async.AwaitTask <| lndClient.SwaggerClient.InitWalletAsync initWalletReq
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        bitcoind.GenerateBlocksToDummyAddress (BlockHeightOffset32 1u)
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "Server listening on 127.0.0.2:9735")
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        return {
            LndDir = lndDir
            ProcessWrapper = processWrapper
            ConnectionString = connectionString
            ClientFactory = clientFactory
        }
    }

    member self.Client(): LndClient =
        self.ClientFactory.Create self.ConnectionString :?> LndClient

    member self.GetEndPoint(): Async<NodeEndPoint> = async {
        let client = self.Client()
        let! getInfo = Async.AwaitTask (client.SwaggerClient.GetInfoAsync())
        return NodeEndPoint.Parse Currency.BTC (SPrintF1 "%s@127.0.0.2:9735" getInfo.Identity_pubkey)
    }

    member self.GetDepositAddress(): Async<BitcoinAddress> =
        let client = self.Client()
        (client :> ILightningClient).GetDepositAddress ()
        |> Async.AwaitTask

    member self.GetBlockHeight(): Async<BlockHeight> = async {
        let client = self.Client()
        let! getInfo = Async.AwaitTask (client.SwaggerClient.GetInfoAsync())
        return BlockHeight (uint32 getInfo.Block_height.Value)
    }

    member self.WaitForBlockHeight(blockHeight: BlockHeight): Async<unit> = async {
        let! currentBlockHeight = self.GetBlockHeight()
        if blockHeight > currentBlockHeight then
            self.ProcessWrapper.WaitForMessage <| fun msg ->
                msg.Contains(SPrintF1 "New block: height=%i" blockHeight.Value)
        return ()
    }

    member self.OnChainBalance(): Async<Money> = async {
        let client = self.Client()
        let! balance = Async.AwaitTask (client.SwaggerClient.WalletBalanceAsync ())
        return Money(uint64 balance.Confirmed_balance, MoneyUnit.Satoshi)
    }

    member self.ChannelBalance(): Async<Money> = async {
        let client = self.Client()
        let! balance = Async.AwaitTask (client.SwaggerClient.ChannelBalanceAsync())
        return Money(uint64 balance.Balance, MoneyUnit.Satoshi)
    }

    member self.WaitForBalance(money: Money): Async<unit> = async {
        let! currentBalance = self.OnChainBalance()
        if money > currentBalance then
            self.ProcessWrapper.WaitForMessage <| fun msg ->
                msg.Contains "[walletbalance]"
            return! self.WaitForBalance money
        return ()
    }

    member self.SendCoins(money: Money) (address: BitcoinAddress) (feerate: FeeRatePerKw): Async<TxId> = async {
        let client = self.Client()
        let sendCoinsReq =
            LnrpcSendCoinsRequest (
                Addr = address.ToString(),
                Amount = (money.ToUnit MoneyUnit.Satoshi).ToString(),
                Sat_per_byte = feerate.Value.ToString()
            )
        let! sendCoinsResp = Async.AwaitTask (client.SwaggerClient.SendCoinsAsync sendCoinsReq)
        return TxId <| uint256 sendCoinsResp.Txid
    }

    member self.SendPayment(invoice: string)
        : Async<unit> =
        async {
            let client = self.Client()
            let sendCoinsReq =
                LnrpcSendRequest (
                    Payment_request = invoice
                )
            let! _sendCoinsResp = Async.AwaitTask (client.SwaggerClient.SendPaymentSyncAsync sendCoinsReq)
            return ()
        }

    member self.CreateInvoice (transferAmount: TransferAmount) (expiryOpt: Option<TimeSpan>)
        : Async<Option<LightningInvoice>> =
        async {
            let amount =
                let btcAmount = transferAmount.ValueToSend
                let lnAmount = int64(btcAmount * decimal DotNetLightning.Utils.LNMoneyUnit.BTC)
                DotNetLightning.Utils.LNMoney lnAmount
            let client = self.Client()
            try
                let expiry = Option.defaultValue (TimeSpan.FromHours 1.) expiryOpt
                let invoiceAmount = LightMoney.MilliSatoshis amount.MilliSatoshi
                let! response =
                    client.CreateInvoice(invoiceAmount, "Test", expiry, CancellationToken.None)
                    |> Async.AwaitTask
                return Some response
            with
            | ex ->
                // BTCPayServer.Lightning is broken and doesn't handle the
                // channel-closed reply from lnd properly. This catches the exception (and
                // hopefully not other, unrelated exceptions).
                // See: https://github.com/btcpayserver/BTCPayServer.Lightning/issues/38
                match FSharpUtil.FindException<Newtonsoft.Json.JsonReaderException> ex with
                | None -> return raise <| FSharpUtil.ReRaise ex
                | Some _ -> return None
        }


    member self.ConnectTo (nodeEndPoint: NodeEndPoint): Async<unit> =
        let client = self.Client()
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let nodeInfo =
            let pubKey =
                let stringified = nodeEndPoint.NodeId.ToString()
                let unstringified = PubKey stringified
                unstringified
            NodeInfo (pubKey, nodeEndPoint.IPEndPoint.Address.ToString(), nodeEndPoint.IPEndPoint.Port)
        let rec tryConnect numRetries = async {
            let! connResult =
                (client :> ILightningClient).ConnectTo nodeInfo
                |> Async.AwaitTask
            Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
            Console.WriteLine(sprintf "*** connResult = %A" connResult)
            match connResult with
            | ConnectionResult.CouldNotConnect ->
                if numRetries <= 0 then
                    Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
                    return failwith "could not connect"
                else
                    Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
                    do! Async.Sleep 5000
                    Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
                    return! tryConnect (numRetries - 1)
            | _ ->
                return ()
        }
        tryConnect 10

    member self.OpenChannel (nodeEndPoint: NodeEndPoint)
                            (amount: Money)
                            (feeRate: FeeRatePerKw)
                                : Async<Result<unit, OpenChannelResult>> = async {
        let client = self.Client()
        let nodeInfo =
            let pubKey =
                let stringified = nodeEndPoint.NodeId.ToString()
                let unstringified = PubKey stringified
                unstringified
            NodeInfo (pubKey, nodeEndPoint.IPEndPoint.Address.ToString(), nodeEndPoint.IPEndPoint.Port)
        let openChannelReq =
            new OpenChannelRequest (
                NodeInfo = nodeInfo,
                ChannelAmount = amount,
                FeeRate = new FeeRate(Money(uint64 feeRate.Value))
            )
        let! openChannelResponse = Async.AwaitTask <| (client :> ILightningClient).OpenChannel openChannelReq
        match openChannelResponse.Result with
        | OpenChannelResult.Ok -> return Ok ()
        | err -> return Error err
    }

    member self.CloseChannel (fundingOutPoint: OutPoint) (force: bool)
        : Async<unit> =
        async {
            let client = self.Client()
            let fundingTxIdStr = fundingOutPoint.Hash.ToString()
            let fundingOutputIndex = fundingOutPoint.N
            try
                let! _response =
                    Async.AwaitTask
                    <| client.SwaggerClient.CloseChannelAsync(fundingTxIdStr, int64 fundingOutputIndex, force)
                return ()
            with
            | ex ->
                // BTCPayServer.Lightning is broken and doesn't handle the
                // channel-closed reply from lnd properly. This catches the exception (and
                // hopefully not other, unrelated exceptions).
                // See: https://github.com/btcpayserver/BTCPayServer.Lightning/issues/38
                match FSharpUtil.FindException<Newtonsoft.Json.JsonReaderException> ex with
                | None -> return raise <| FSharpUtil.ReRaise ex
                | Some _ -> return ()
        }


    member self.FundByMining (bitcoind: Bitcoind)
                                 : Async<unit> = async {
        let! lndDepositAddress = self.GetDepositAddress()
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        let blocksMinedToLnd = BlockHeightOffset32 1u
        bitcoind.GenerateBlocks blocksMinedToLnd lndDepositAddress
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)

        // Geewallet cannot use these outputs, even though they are encumbered with an output
        // script from its wallet. This is because they come from coinbase. Coinbase outputs are
        // the source of all bitcoin, and as of May 2020, Geewallet does not detect coins
        // received straight from coinbase. In practice, this doesn't matter, since miners
        // do not use Geewallet. If the coins were to be detected by geewallet,
        // this test would still work. This comment is just here to avoid confusion.
        let maturityDurationInNumberOfBlocks = BlockHeightOffset32 (uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity)

        let someMinerThrowAwayAddress =
            use key = new Key()
            key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
        bitcoind.GenerateBlocks maturityDurationInNumberOfBlocks someMinerThrowAwayAddress
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)

        // We confirm the one block mined to LND, by waiting for LND to see the chain
        // at a height which has that block matured. The height at which the block will
        // be matured is 100 on regtest. Since we initialally mined one block for LND,
        // this will wait until the block height of LND reaches 1 (initial blocks mined)
        // plus 100 blocks (coinbase maturity). This test has been parameterized
        // to use the constants defined in NBitcoin, but you have to keep in mind that
        // the coinbase maturity may be defined differently in other coins.
        do! self.WaitForBlockHeight (BlockHeight.Zero + blocksMinedToLnd + maturityDurationInNumberOfBlocks)
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
        do! self.WaitForBalance (Money(50UL, MoneyUnit.BTC))
        Console.WriteLine(sprintf "*** line %s of %s" __LINE__ __SOURCE_FILE__)
    }

