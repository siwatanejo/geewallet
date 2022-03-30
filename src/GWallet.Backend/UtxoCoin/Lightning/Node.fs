namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.IO
open System.Net
open System.Linq

open NBitcoin
open DotNetLightning.Chain
open DotNetLightning.Channel
open DotNetLightning.Channel.ForceCloseFundsRecovery
open DotNetLightning.Crypto
open DotNetLightning.Utils
open DotNetLightning.Serialization.Msgs
open DotNetLightning.Payment
open ResultUtils.Portability
open NOnion.Directory
open NOnion.Network

open GWallet.Backend
open GWallet.Backend.UtxoCoin
open GWallet.Backend.UtxoCoin.Account
open GWallet.Backend.UtxoCoin.Lightning.Validation
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks

type internal NodeOpenChannelError =
    | Connect of ConnectError
    | InitMsgValidation of InitMsgValidationError
    | OpenChannel of OpenChannelError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Connect connectError ->
                SPrintF1 "error connecting: %s" (connectError :> IErrorMsg).Message
            | InitMsgValidation validationError ->
                SPrintF1 "invalid remote party initialization: %s" (validationError :> IErrorMsg).Message
            | OpenChannel openChannelError ->
                SPrintF1 "error opening channel: %s" (openChannelError :> IErrorMsg).Message
        member __.ChannelBreakdown: bool =
            false

type internal NodeAcceptChannelError =
    | AcceptPeer of ConnectError
    | InitMsgValidation of InitMsgValidationError
    | AcceptChannel of AcceptChannelError
    interface IErrorMsg with
        member self.Message =
            match self with
            | AcceptPeer connectError ->
                SPrintF1 "error accepting connection: %s" (connectError :> IErrorMsg).Message
            | InitMsgValidation validationError ->
                SPrintF1 "invalid remote party initialization: %s" (validationError :> IErrorMsg).Message
            | AcceptChannel acceptChannelError ->
                SPrintF1 "error accepting channel: %s" (acceptChannelError :> IErrorMsg).Message
        member __.ChannelBreakdown: bool =
            false

type internal NodeSendMonoHopPaymentError =
    | Reconnect of ReconnectActiveChannelError
    | SendPayment of SendMonoHopPaymentError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | SendPayment sendMonoHopPaymentError ->
                SPrintF1 "error sending payment on reconnected channel: %s"
                         (sendMonoHopPaymentError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown
            | SendPayment sendMonoHopPaymentError ->
                (sendMonoHopPaymentError :> IErrorMsg).ChannelBreakdown

type internal NodeSendHtlcPaymentError =
    | Reconnect of ReconnectActiveChannelError
    | SendPayment of SendHtlcPaymentError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | SendPayment sendHtlcPaymentError ->
                SPrintF1 "error sending payment on reconnected channel: %s"
                         (sendHtlcPaymentError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown
            | SendPayment sendHtlcPaymentError ->
                (sendHtlcPaymentError :> IErrorMsg).ChannelBreakdown

type internal NodeReceiveMonoHopPaymentError =
    | Reconnect of ReconnectActiveChannelError
    | ReceivePayment of RecvMonoHopPaymentError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | ReceivePayment recvMonoHopPaymentError ->
                SPrintF1 "error receiving payment on reconnected channel: %s"
                         (recvMonoHopPaymentError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown
            | ReceivePayment recvMonoHopPaymentError ->
                (recvMonoHopPaymentError :> IErrorMsg).ChannelBreakdown


type internal NodeReceiveHtlcPaymentError =
    | Reconnect of ReconnectActiveChannelError
    | ReceivePayment of RecvHtlcPaymentError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | ReceivePayment recvHtlcPaymentError ->
                SPrintF1 "error receiving payment on reconnected channel: %s"
                         (recvHtlcPaymentError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown
            | ReceivePayment recvHtlcPaymentError ->
                (recvHtlcPaymentError :> IErrorMsg).ChannelBreakdown

type NodeInitiateCloseChannelError =
    | Reconnect of IErrorMsg
    | InitiateCloseChannel of IErrorMsg
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1
                    "error reconnecting channel: %s"
                    reconnectActiveChannelError.Message
            | InitiateCloseChannel closeChannelError ->
                SPrintF1
                    "error initiating channel-closing on reconnected channel: %s"
                    closeChannelError.Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                reconnectActiveChannelError.ChannelBreakdown
            | InitiateCloseChannel closeChannelError ->
                closeChannelError.ChannelBreakdown

type internal NodeAcceptCloseChannelError =
    | Reconnect of ReconnectActiveChannelError
    | AcceptCloseChannel of CloseChannelError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | AcceptCloseChannel acceptCloseChannelError ->
                SPrintF1 "error accepting channel close on reconnected channel: %s"
                         (acceptCloseChannelError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown
            | AcceptCloseChannel acceptCloseChannelError ->
                (acceptCloseChannelError :> IErrorMsg).ChannelBreakdown

type internal NodeReceiveLightningEventError =
    | Reconnect of ReconnectActiveChannelError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Reconnect reconnectActiveChannelError ->
                (reconnectActiveChannelError :> IErrorMsg).ChannelBreakdown

type internal NodeUpdateFeeError =
    | Reconnect of ReconnectActiveChannelError
    | UpdateFee of UpdateFeeError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | UpdateFee updateFeeError ->
                SPrintF1 "error updating fee: %s" (updateFeeError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            (self :> IErrorMsg).ChannelBreakdown

type internal NodeAcceptUpdateFeeError =
    | Reconnect of ReconnectActiveChannelError
    | AcceptUpdateFee of AcceptUpdateFeeError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Reconnect reconnectActiveChannelError ->
                SPrintF1 "error reconnecting channel: %s" (reconnectActiveChannelError :> IErrorMsg).Message
            | AcceptUpdateFee acceptUpdateFeeError ->
                SPrintF1 "error accepting updating fee: %s" (acceptUpdateFeeError :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            (self :> IErrorMsg).ChannelBreakdown

type IChannelToBeOpened =
    abstract member ConfirmationsRequired: uint32 with get

type IncomingChannelEvent =
    | HtlcPayment of isSuccess: bool
    | MonoHopUnidirectionalPayment
    | Shutdown

type PendingChannel internal (outgoingUnfundedChannel: OutgoingUnfundedChannel) =

    member internal self.OutgoingUnfundedChannel = outgoingUnfundedChannel

    member __.Currency: Currency =
        (outgoingUnfundedChannel.Account :> IAccount).Currency

    member self.FundingDestinationString(): string =
        let network = UtxoCoin.Account.GetNetwork self.Currency
        (outgoingUnfundedChannel.FundingDestination.ScriptPubKey.GetDestinationAddress network).ToString()

    member __.TransferAmount: TransferAmount = outgoingUnfundedChannel.TransferAmount

    member public self.AcceptWithFundingTx (fundingTransactionHex: string)
                                               : Async<Result<ChannelIdentifier * TransactionIdentifier, IErrorMsg>> = async {
        let! fundedChannelRes =
            let account = self.OutgoingUnfundedChannel.Account
            let network = Account.GetNetwork (account :> IAccount).Currency
            let hex = DataEncoders.HexEncoder()
            let fundingTransaction =
                Transaction.Load (hex.DecodeData fundingTransactionHex, network)
            FundedChannel.FundChannel self.OutgoingUnfundedChannel fundingTransaction
        match fundedChannelRes with
        | Error fundChannelError ->
            if fundChannelError.PossibleBug then
                let msg = SPrintF1 "Error funding channel: %s" (fundChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error (fundChannelError :> IErrorMsg)
        | Ok fundedChannel ->
            let txId = fundedChannel.FundingTxId
            let channelId = fundedChannel.ChannelId
            (fundedChannel :> IDisposable).Dispose()
            return Ok (channelId, txId)
    }

    member public self.Accept (metadata: TransactionMetadata)
                              (password: string)
                                  : Async<Result<ChannelIdentifier * TransactionIdentifier, IErrorMsg>> =
        let account = self.OutgoingUnfundedChannel.Account
        let transaction =
            Account.SignTransactionForDestination
                account
                metadata
                self.OutgoingUnfundedChannel.FundingDestination
                self.TransferAmount
                password
        self.AcceptWithFundingTx transaction

    interface IChannelToBeOpened with
        member self.ConfirmationsRequired
            with get(): uint32 =
                self.OutgoingUnfundedChannel.MinimumDepth.Value

type ClosingBalanceBelowDustLimitError =
    | ClosingBalanceBelowDustLimit

type NodeClient internal (channelStore: ChannelStore, nodeMasterPrivKey: NodeMasterPrivKey) =
    member val ChannelStore = channelStore
    member val internal NodeMasterPrivKey = nodeMasterPrivKey
    member val internal NodeSecret = nodeMasterPrivKey.NodeSecret()
    member val Account = channelStore.Account

    static member internal AccountPrivateKeyToNodeSecret (accountKey: Key) =
        NBitcoin.ExtKey.CreateFromSeed (accountKey.ToBytes ())

    member internal self.OpenChannel (nodeIdentifier: NodeIdentifier)
                                     (channelCapacity: TransferAmount)
                                         : Async<Result<PendingChannel, IErrorMsg>> = async {
        let! connectRes =
            PeerNode.Connect
                nodeMasterPrivKey
                nodeIdentifier
                ((self.Account :> IAccount).Currency)
                (Money(channelCapacity.ValueToSend, MoneyUnit.BTC))
        match connectRes with
        | Error connectError ->
            if connectError.PossibleBug then
                let msg =
                    SPrintF3
                        "error connecting to peer %s to open channel of capacity %M: %s"
                        (nodeIdentifier.ToString())
                        channelCapacity.ValueToSend
                        (connectError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore
            return Error <| (NodeOpenChannelError.Connect connectError :> IErrorMsg)
        | Ok peerNode ->
            match ValidateRemoteInitMsg peerNode.InitMsg with
            | Error validationError ->
                return Error <| (NodeOpenChannelError.InitMsgValidation validationError :> IErrorMsg)
            | Ok _ ->
                let! outgoingUnfundedChannelRes =
                    OutgoingUnfundedChannel.OpenChannel
                        peerNode
                        self.Account
                        channelCapacity
                match outgoingUnfundedChannelRes with
                | Error openChannelError ->
                    if openChannelError.PossibleBug then
                        let msg =
                            SPrintF3
                                "error opening channel with peer %s of capacity %M: %s"
                                (nodeIdentifier.ToString())
                                channelCapacity.ValueToSend
                                (openChannelError :> IErrorMsg).Message
                        Infrastructure.ReportWarningMessage msg
                        |> ignore<bool>
                    return Error (NodeOpenChannelError.OpenChannel openChannelError :> IErrorMsg)
                | Ok outgoingUnfundedChannel ->
                    return Ok <| PendingChannel outgoingUnfundedChannel
    }

    member internal self.SendHtlcPayment (channelId: ChannelIdentifier)
                                         (invoice: PaymentInvoice)
                                             : Async<Result<unit, IErrorMsg>> = async {
        let! activeChannelRes =
            ActiveChannel.ConnectReestablish self.ChannelStore nodeMasterPrivKey channelId None
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error connecting to peer to send hltc payment on channel %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeSendHtlcPaymentError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel ->
            let! paymentRes = activeChannel.SendHtlcPayment invoice
            match paymentRes with
            | Error sendHtlcPaymentError ->
                if sendHtlcPaymentError.PossibleBug then
                    let msg =
                        SPrintF2
                            "error sending hltc payment on channel %s: %s"
                            (channelId.ToString())
                            (sendHtlcPaymentError :> IErrorMsg).Message
                    Infrastructure.ReportWarningMessage msg
                    |> ignore<bool>
                return Error <| (NodeSendHtlcPaymentError.SendPayment sendHtlcPaymentError :> IErrorMsg)
            | Ok activeChannelAfterPayment ->
                (activeChannelAfterPayment :> IDisposable).Dispose()
                return Ok ()


    }

    member internal self.SendMonoHopPayment (channelId: ChannelIdentifier)
                                            (transferAmount: TransferAmount)
                                            (nonionEndPoint: Option<NOnionEndPoint>)
                                                : Async<Result<unit, IErrorMsg>> = async {
        let amount =
            let btcAmount = transferAmount.ValueToSend
            let lnAmount = int64(btcAmount * decimal DotNetLightning.Utils.LNMoneyUnit.BTC)
            DotNetLightning.Utils.LNMoney lnAmount
        let! activeChannelRes =
            ActiveChannel.ConnectReestablish self.ChannelStore nodeMasterPrivKey channelId nonionEndPoint
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error connecting to peer to send monohop payment on channel %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeSendMonoHopPaymentError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel ->
            let! paymentRes = activeChannel.SendMonoHopUnidirectionalPayment amount
            match paymentRes with
            | Error sendMonoHopPaymentError ->
                if sendMonoHopPaymentError.PossibleBug then
                    let msg =
                        SPrintF2
                            "error sending monohop payment on channel %s: %s"
                            (channelId.ToString())
                            (sendMonoHopPaymentError :> IErrorMsg).Message
                    Infrastructure.ReportWarningMessage msg
                    |> ignore<bool>
                return Error <| (NodeSendMonoHopPaymentError.SendPayment sendMonoHopPaymentError :> IErrorMsg)
            | Ok activeChannelAfterPayment ->
                (activeChannelAfterPayment :> IDisposable).Dispose()
                return Ok ()
    }

    member internal self.InitiateCloseChannel (channelId: ChannelIdentifier) (nonionIntroductionPoint: Option<NOnionEndPoint>): Async<Result<unit, NodeInitiateCloseChannelError>> =
        async {
            let! connectRes =
                ActiveChannel.ConnectReestablish self.ChannelStore self.NodeMasterPrivKey channelId nonionIntroductionPoint
            match connectRes with
            | Error connectError ->
                return Error <| NodeInitiateCloseChannelError.Reconnect (connectError :> IErrorMsg)
            | Ok activeChannel ->
                let! closeRes = ClosedChannel.InitiateCloseChannel activeChannel.ConnectedChannel
                match closeRes with
                | Error closeError ->
                    return Error <| NodeInitiateCloseChannelError.InitiateCloseChannel (closeError :> IErrorMsg)
                | Ok _ ->
                    return Ok ()
        }

    member internal self.ConnectLockChannelFunding (channelId: ChannelIdentifier)
                                                   (nonionIntroductionPoint: Option<NOnionEndPoint>)
                                                       : Async<Result<unit, IErrorMsg>> =
        async {
            let! activeChannelRes =
                ActiveChannel.ConnectReestablish
                    self.ChannelStore
                    nodeMasterPrivKey
                    channelId
                    nonionIntroductionPoint
            match activeChannelRes with
            | Error reconnectActiveChannelError ->
                if reconnectActiveChannelError.PossibleBug then
                    let msg =
                        SPrintF2
                            "error reconnecting to peer to lock channel %s: %s"
                            (channelId.ToString())
                            (reconnectActiveChannelError :> IErrorMsg).Message
                    Infrastructure.ReportWarningMessage msg
                    |> ignore<bool>
                return Error (reconnectActiveChannelError :> IErrorMsg)
            | Ok activeChannel ->
                (activeChannel :> IDisposable).Dispose()
                return Ok ()
        }


type NodeServer internal (channelStore: ChannelStore, transportListener: TransportListener) =
    member val ChannelStore = channelStore
    member val internal TransportListener = transportListener
    member val internal NodeMasterPrivKey = transportListener.NodeMasterPrivKey
    member val internal NodeId = transportListener.NodeId
    member val EndPoint = transportListener.EndPoint
    member val Account = channelStore.Account

    interface IDisposable with
        member self.Dispose() =
            (self.TransportListener :> IDisposable).Dispose()

    member internal self.AcceptChannel (): Async<Result<(ChannelIdentifier * TransactionIdentifier), IErrorMsg>> = async {
        let! acceptPeerRes =
            PeerNode.AcceptAnyFromTransportListener
                self.TransportListener
                self.ChannelStore.Currency
                None
        match acceptPeerRes with
        | Error connectError ->
            if connectError.PossibleBug then
                let msg =
                    SPrintF1
                        "error accepting connection from peer to accept channel: %s"
                        (connectError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeAcceptChannelError.AcceptPeer connectError :> IErrorMsg)
        | Ok peerNode ->
            match ValidateRemoteInitMsg peerNode.InitMsg with
            | Error validationError ->
                return Error (NodeOpenChannelError.InitMsgValidation validationError :> IErrorMsg)
            | Ok _ ->
                let! fundedChannelRes = FundedChannel.AcceptChannel peerNode self.Account
                match fundedChannelRes with
                | Error acceptChannelError ->
                    if acceptChannelError.PossibleBug then
                        let msg =
                            SPrintF2
                                "error accepting channel from peer %s: %s"
                                (peerNode.NodeEndPoint.ToString())
                                (acceptChannelError :> IErrorMsg).Message
                        Infrastructure.ReportWarningMessage msg
                        |> ignore<bool>
                    return Error <| (NodeAcceptChannelError.AcceptChannel acceptChannelError :> IErrorMsg)
                | Ok fundedChannel ->
                    let channelId = fundedChannel.ChannelId
                    let txId = fundedChannel.FundingTxId
                    (fundedChannel :> IDisposable).Dispose()
                    return Ok (channelId, txId)
    }

    // use ReceiveLightningEvent instead
    member internal self.ReceiveMonoHopPayment (channelId: ChannelIdentifier)
                                                   : Async<Result<unit, IErrorMsg>> = async {
        let! activeChannelRes = ActiveChannel.AcceptReestablish self.ChannelStore self.TransportListener channelId
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error accepting connection from peer to receive monohop payment on channel %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeReceiveMonoHopPaymentError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel ->
            let connectedChannel = activeChannel.ConnectedChannel
            Infrastructure.LogDebug "Waiting for lightning message"
            let rec recv (peerNode: PeerNode) =
                async {
                    let! recvChannelMsgRes = peerNode.RecvChannelMsg()
                    match recvChannelMsgRes with
                    | Error err ->
                        return failwith <| SPrintF1 "Received error while waiting for lightning message: %s" (err :> IErrorMsg).Message
                    | Ok (peerNodeAfterMsgReceived, channelMsg) ->
                        match channelMsg with
                        | :? DotNetLightning.Serialization.Msgs.MonoHopUnidirectionalPaymentMsg as monoHopUnidirectionalPaymentMsg ->
                            let activeChannelAfterMsgReceived =
                                { activeChannel with
                                    ConnectedChannel =
                                        { activeChannel.ConnectedChannel
                                            with PeerNode = peerNodeAfterMsgReceived }}

                            return! self.HandleMonoHopUnidirectionalPaymentMsg activeChannelAfterMsgReceived channelId monoHopUnidirectionalPaymentMsg
                        | :? FundingLockedMsg ->
                            return! recv(peerNodeAfterMsgReceived)
                        | msg ->
                            return failwith <| SPrintF1 "Unexpected msg while waiting for monohop payment message: %As" msg
                }
            return! recv(connectedChannel.PeerNode)
    }

    member private self.HandleMonoHopUnidirectionalPaymentMsg (activeChannel: ActiveChannel)
        (channelId: ChannelIdentifier)
        (monoHopUnidirectionalPaymentMsg: DotNetLightning.Serialization.Msgs.MonoHopUnidirectionalPaymentMsg)
        : Async<Result<unit, IErrorMsg>> =
            async {
                let! paymentRes = activeChannel.RecvMonoHopUnidirectionalPayment monoHopUnidirectionalPaymentMsg
                match paymentRes with
                | Error recvMonoHopPaymentError ->
                    if recvMonoHopPaymentError.PossibleBug then
                        let msg =
                            SPrintF2
                                "error accepting monohop payment on channel %s: %s"
                                (channelId.ToString())
                                (recvMonoHopPaymentError :> IErrorMsg).Message
                        Infrastructure.ReportWarningMessage msg
                        |> ignore<bool>
                    return Error <| (NodeReceiveMonoHopPaymentError.ReceivePayment recvMonoHopPaymentError :> IErrorMsg)
                | Ok activeChannelAfterPaymentReceived ->
                    (activeChannelAfterPaymentReceived :> IDisposable).Dispose()
                    return Ok ()
            }

    member private self.HandleUpdateAddHtlcMsg (activeChannel: ActiveChannel)
        (channelId: ChannelIdentifier)
        (updateAddHtlcMsg: DotNetLightning.Serialization.Msgs.UpdateAddHTLCMsg)
        : Async<Result<bool, IErrorMsg>> =
            async {
                let! paymentRes = activeChannel.RecvHtlcPayment updateAddHtlcMsg
                match paymentRes with
                | Error recvHtlcPaymentError ->
                    if recvHtlcPaymentError.PossibleBug then
                        let msg =
                            SPrintF2
                                "error accepting htlc on channel %s: %s"
                                (channelId.ToString())
                                (recvHtlcPaymentError :> IErrorMsg).Message
                        Infrastructure.ReportWarningMessage msg
                        |> ignore<bool>
                    return Error <| (NodeReceiveHtlcPaymentError.ReceivePayment recvHtlcPaymentError :> IErrorMsg)
                | Ok (activeChannelAfterPaymentReceived, wasSuccess) ->
                    (activeChannelAfterPaymentReceived :> IDisposable).Dispose()
                    return Ok wasSuccess
            }

    // use ReceiveLightningEvent instead
    member internal self.AcceptCloseChannel (channelId: ChannelIdentifier)
                                                : Async<Result<unit, IErrorMsg>> = async {
        let! activeChannelRes = ActiveChannel.AcceptReestablish self.ChannelStore self.TransportListener channelId
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error accepting connection from peer to accept close channel on channel %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeAcceptCloseChannelError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel ->
            let connectedChannel = activeChannel.ConnectedChannel

            Infrastructure.LogDebug "Waiting for lightning message"
            let rec recv (peerNode: PeerNode) =
                async {
                    let! recvChannelMsgRes = peerNode.RecvChannelMsg()
                    match recvChannelMsgRes with
                    | Error err ->
                        return failwith <| SPrintF1 "Received error while waiting for lightning message: %s" (err :> IErrorMsg).Message
                    | Ok (peerNodeAfterMsgReceived, channelMsg) ->
                        match channelMsg with
                        | :? DotNetLightning.Serialization.Msgs.ShutdownMsg as shutdownMsg ->
                            let activeChannelAfterMsgReceived =
                                { activeChannel with
                                    ConnectedChannel =
                                        { activeChannel.ConnectedChannel with
                                            PeerNode = peerNodeAfterMsgReceived
                                        }
                                }

                            return! self.HandleShutdownMsg activeChannelAfterMsgReceived shutdownMsg
                        | :? FundingLockedMsg ->
                            return! recv (peerNodeAfterMsgReceived)
                        | msg ->
                            return failwith <| SPrintF1 "Unexpected msg while waiting for shutdown message: %As" msg
                }
            return! recv (connectedChannel.PeerNode)
    }

    member private self.HandleShutdownMsg (activeChannel: ActiveChannel)
                                          (shutdownMsg: DotNetLightning.Serialization.Msgs.ShutdownMsg)
                                              : Async<Result<unit, IErrorMsg>> = async {
        let! closeRes = ClosedChannel.AcceptCloseChannel (activeChannel.ConnectedChannel, shutdownMsg)
        match closeRes with
        | Error acceptCloseChannelError ->
            return Error <| (NodeAcceptCloseChannelError.AcceptCloseChannel acceptCloseChannelError :> IErrorMsg)
        | Ok _ ->
            return Ok ()
    }

    member internal self.AcceptLockChannelFunding (channelId: ChannelIdentifier)
                                                      : Async<Result<unit, IErrorMsg>> =
        async {
            let! activeChannelRes =
                ActiveChannel.AcceptReestablish
                    self.ChannelStore
                    self.TransportListener
                    channelId
            match activeChannelRes with
            | Error reconnectActiveChannelError ->
                if reconnectActiveChannelError.PossibleBug then
                    let msg =
                        SPrintF2
                            "error reconnecting to peer to lock channel %s: %s"
                            (channelId.ToString())
                            (reconnectActiveChannelError :> IErrorMsg).Message
                    Infrastructure.ReportWarningMessage msg
                    |> ignore<bool>
                return Error (reconnectActiveChannelError :> IErrorMsg)
            | Ok activeChannel ->
                (activeChannel :> IDisposable).Dispose()
                return Ok ()
        }

    member internal self.ReceiveLightningEvent (channelId: ChannelIdentifier)
                                                   : Async<Result<IncomingChannelEvent, IErrorMsg>> = async {
        let rec receiveEvent (activeChannel: ActiveChannel) = async {
            Infrastructure.LogDebug "Waiting for lightning message"
            let connectedChannel = activeChannel.ConnectedChannel
            let peerNode = connectedChannel.PeerNode
            let! recvChannelMsgRes = peerNode.RecvChannelMsg()
            match recvChannelMsgRes with
            | Error err ->
                return failwith <| SPrintF1 "Received error while waiting for lightning message: %s" (err :> IErrorMsg).Message
            | Ok (peerNodeAfterMsgReceived, channelMsg) ->
                let connectedChannelAfterMsgReceived = {
                    connectedChannel with
                        PeerNode = peerNodeAfterMsgReceived
                }
                let activeChannelAfterMsgReceived = {
                    activeChannel with
                        ConnectedChannel = connectedChannelAfterMsgReceived
                }
                match channelMsg with
                | :? MonoHopUnidirectionalPaymentMsg as monoHopUnidirectionalPaymentMsg ->
                    let! res = self.HandleMonoHopUnidirectionalPaymentMsg activeChannelAfterMsgReceived channelId monoHopUnidirectionalPaymentMsg
                    match res with
                    | Error err -> return Error err
                    | Ok () -> return Ok IncomingChannelEvent.MonoHopUnidirectionalPayment
                | :? UpdateAddHTLCMsg as updateAddHTLCMsg ->
                    let! res = self.HandleUpdateAddHtlcMsg activeChannelAfterMsgReceived channelId updateAddHTLCMsg
                    match res with
                    | Error err -> return Error err
                    | Ok wasSucess -> return Ok <| IncomingChannelEvent.HtlcPayment wasSucess
                | :? ShutdownMsg as shutdownMsg ->
                    let! res = self.HandleShutdownMsg activeChannelAfterMsgReceived shutdownMsg
                    match res with
                    | Error err -> return Error err
                    | Ok () -> return Ok IncomingChannelEvent.Shutdown
                | _ ->
                    Infrastructure.LogDebug <| SPrintF2 "Ignoring this msg (%A): %A" (channelMsg.GetType()) channelMsg
                    return! receiveEvent activeChannelAfterMsgReceived
        }
        let! activeChannelRes = ActiveChannel.AcceptReestablish self.ChannelStore self.TransportListener channelId
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error accepting connection from peer to receive lightning event on channel %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error <| (NodeReceiveLightningEventError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel -> return! receiveEvent activeChannel
    }

    member internal self.AcceptUpdateFee (channelId: ChannelIdentifier): Async<Result<unit, IErrorMsg>> = async {
        let serializedChannel = self.ChannelStore.LoadChannel channelId
        if serializedChannel.IsFunder() then
            return failwith "AcceptUpdateFee called on non-fundee channel"
        else
            let! activeChannelRes =
                ActiveChannel.AcceptReestablish
                    self.ChannelStore
                    self.TransportListener
                    channelId
            match activeChannelRes with
            | Error reconnectActiveChannelError ->
                if reconnectActiveChannelError.PossibleBug then
                    let msg =
                        SPrintF2
                            "error connecting to peer to accept update fee %s: %s"
                            (channelId.ToString())
                            (reconnectActiveChannelError :> IErrorMsg).Message
                    Infrastructure.ReportWarningMessage msg
                    |> ignore<bool>
                return Error (NodeAcceptUpdateFeeError.Reconnect reconnectActiveChannelError :> IErrorMsg)
            | Ok activeChannel ->
                let! activeChannelAfterUpdateFeeRes = activeChannel.AcceptUpdateFee()
                match activeChannelAfterUpdateFeeRes with
                | Error acceptUpdateFeeError ->
                    return Error (NodeAcceptUpdateFeeError.AcceptUpdateFee acceptUpdateFeeError :> IErrorMsg)
                | Ok activeChannelAfterUpdateFee ->
                    (activeChannelAfterUpdateFee :> IDisposable).Dispose()
                    return Ok ()
    }

type Node =
    | Client of NodeClient
    | Server of NodeServer

    member self.ChannelStore =
        match self with
        | Client nodeClient -> nodeClient.ChannelStore
        | Server nodeServer -> nodeServer.ChannelStore

    member self.Account =
        match self with
        | Client nodeClient -> nodeClient.Account
        | Server nodeServer -> nodeServer.Account
        :> IAccount

    member self.CreateAnchorFeeBumpForForceClose
        (channelId: ChannelIdentifier)
        (commitmentTxString: string)
        (password: string)
        : Async<Result<FeeBumpTx, ClosingBalanceBelowDustLimitError>> =
            async {
                let nodeMasterPrivKey =
                    match self with
                    | Client nodeClient -> nodeClient.NodeMasterPrivKey
                    | Server nodeServer -> nodeServer.NodeMasterPrivKey
                let currency = self.Account.Currency
                let serializedChannel = self.ChannelStore.LoadChannel channelId
                let! feeBumpTransaction = async {
                    let network =
                        UtxoCoin.Account.GetNetwork currency
                    let commitmentTx =
                        Transaction.Parse(commitmentTxString, network)
                    let channelPrivKeys =
                        let channelIndex = serializedChannel.ChannelIndex
                        nodeMasterPrivKey.ChannelPrivKeys channelIndex
                    let targetAddress =
                        let originAddress = self.Account.PublicAddress
                        BitcoinAddress.Create(originAddress, network)
                    let transactionBuilderResult =
                        ForceCloseFundsRecovery.tryClaimAnchorFromCommitmentTx
                            channelPrivKeys
                            serializedChannel.SavedChannelState.StaticChannelConfig
                            commitmentTx
                    match transactionBuilderResult with
                    | Error (LocalAnchorRecoveryError.InvalidCommitmentTx invalidCommitmentTxError) ->
                        return failwith ("invalid commitment tx for force-closing: " + invalidCommitmentTxError.Message)
                    | Error LocalAnchorRecoveryError.AnchorNotFound ->
                        self.ChannelStore.ArchiveChannel channelId
                        return Error <| ClosingBalanceBelowDustLimit
                    | Ok transactionBuilder ->
                        transactionBuilder.SendAllRemaining targetAddress |> ignore<TransactionBuilder>

                        let! transaction, fees =
                            FeeManagement.addFeeInputsWithCpfpSupport
                                (self.Account, password)
                                transactionBuilder
                                (Some (commitmentTx, serializedChannel.FundingScriptCoin()))

                        let feeBumpTransaction: FeeBumpTx =
                            {
                                ChannelId = channelId
                                Currency = currency
                                Fee = MinerFee (fees.Satoshi, DateTime.UtcNow, currency)
                                Tx =
                                    {
                                        NBitcoinTx = transaction
                                    }
                            }
                        return Ok feeBumpTransaction
                }
                return feeBumpTransaction
            }

    member self.CreateRecoveryTxForLocalForceClose
        (channelId: ChannelIdentifier)
        (commitmentTxString: string)
        : Async<Result<RecoveryTx, ClosingBalanceBelowDustLimitError>> =
            async {
                let nodeMasterPrivKey =
                    match self with
                    | Client nodeClient -> nodeClient.NodeMasterPrivKey
                    | Server nodeServer -> nodeServer.NodeMasterPrivKey
                let currency = self.Account.Currency
                let serializedChannel = self.ChannelStore.LoadChannel channelId
                let! recoveryTransactionString = async {
                    let network =
                        UtxoCoin.Account.GetNetwork currency
                    let commitmentTx =
                        Transaction.Parse(commitmentTxString, network)
                    let channelPrivKeys =
                        let channelIndex = serializedChannel.ChannelIndex
                        nodeMasterPrivKey.ChannelPrivKeys channelIndex
                    let targetAddress =
                        let originAddress = self.Account.PublicAddress
                        BitcoinAddress.Create(originAddress, network)
                    let! feeRate = async {
                        let! feeEstimator = FeeEstimator.Create currency
                        return feeEstimator.FeeRatePerKw
                    }
                    let transactionBuilderResult =
                        ForceCloseFundsRecovery.tryGetFundsFromLocalCommitmentTx
                            channelPrivKeys
                            serializedChannel.SavedChannelState.StaticChannelConfig
                            commitmentTx
                    match transactionBuilderResult with
                    | Error (LocalCommitmentTxRecoveryError.InvalidCommitmentTx invalidCommitmentTxError) ->
                        return failwith ("invalid commitment tx for force-closing: " + invalidCommitmentTxError.Message)
                    | Error LocalCommitmentTxRecoveryError.BalanceBelowDustLimit ->
                        return Error <| ClosingBalanceBelowDustLimit
                    | Ok transactionBuilder ->
                        transactionBuilder.SendAll targetAddress |> ignore
                        let fee = transactionBuilder.EstimateFees (feeRate.AsNBitcoinFeeRate())
                        transactionBuilder.SendFees fee |> ignore
                        let recoveryTransaction: RecoveryTx =
                            {
                                ChannelId = channelId
                                Currency = currency
                                Fee = MinerFee (fee.Satoshi, DateTime.UtcNow, currency)
                                Tx =
                                    {
                                        NBitcoinTx = transactionBuilder.BuildTransaction true
                                    }
                            }
                        return Ok recoveryTransaction
                }
                return recoveryTransactionString
            }

    member self.ForceCloseChannel
        (channelId: ChannelIdentifier)
        : Async<Result<string, ClosingBalanceBelowDustLimitError>> =
        async {
            let commitmentTxString = self.ChannelStore.GetCommitmentTx channelId
            let serializedChannel = self.ChannelStore.LoadChannel channelId
            let! forceCloseTxId = UtxoCoin.Account.BroadcastRawTransaction self.Account.Currency commitmentTxString
            // This should still be done once here to make sure local output isn't dust
            let! recoveryTxResult = self.CreateRecoveryTxForLocalForceClose channelId commitmentTxString
            match recoveryTxResult with
            | Error err ->
                self.ChannelStore.ArchiveChannel channelId
                return Error err
            | Ok _recoveryTx ->
                let newSerializedChannel = {
                    serializedChannel with
                        ForceCloseTxIdOpt =
                            TransactionIdentifier.Parse forceCloseTxId
                            |> Some
                        ClosingTimestampUtc = Some DateTime.UtcNow
                }
                self.ChannelStore.SaveChannel newSerializedChannel
                return Ok forceCloseTxId
        }

    member self.CreateRecoveryTxForRemoteForceClose
        (channelId: ChannelIdentifier)
        (closingTx: ForceCloseTx)
        : Async<Result<RecoveryTx, ClosingBalanceBelowDustLimitError>> =
        async {
            let nodeMasterPrivKey =
                match self with
                | Client nodeClient -> nodeClient.NodeMasterPrivKey
                | Server nodeServer -> nodeServer.NodeMasterPrivKey
            let serializedChannel = self.ChannelStore.LoadChannel channelId
            let currency = self.Account.Currency
            let network = UtxoCoin.Account.GetNetwork currency
            let channelPrivKeys =
                let channelIndex = serializedChannel.ChannelIndex
                nodeMasterPrivKey.ChannelPrivKeys channelIndex
            let targetAddress =
                let originAddress = self.Account.PublicAddress
                BitcoinAddress.Create(originAddress, network)
            let! feeRate = async {
                let! feeEstimator = FeeEstimator.Create currency
                return feeEstimator.FeeRatePerKw
            }
            let transactionBuilderResult =
                ForceCloseFundsRecovery.tryGetFundsFromRemoteCommitmentTx
                    channelPrivKeys
                    serializedChannel.SavedChannelState
                    closingTx.Tx.NBitcoinTx

            match transactionBuilderResult with
            | Error (RemoteCommitmentTxRecoveryError.InvalidCommitmentTx invalidCommitmentTxError) ->
                return failwith ("invalid commitment tx for creating recovery tx: " + invalidCommitmentTxError.Message)
            | Error (CommitmentNumberFromTheFuture commitmentNumber) ->
                return failwith (SPrintF1 "commitment number of tx is from the future (%s)" (commitmentNumber.ToString()))
            | Error RemoteCommitmentTxRecoveryError.BalanceBelowDustLimit ->
                self.ChannelStore.ArchiveChannel channelId
                return Error <| ClosingBalanceBelowDustLimit
            | Ok transactionBuilder ->
                transactionBuilder.SendAll targetAddress |> ignore
                let fee = transactionBuilder.EstimateFees (feeRate.AsNBitcoinFeeRate())
                transactionBuilder.SendFees fee |> ignore
                let recoveryTransaction: RecoveryTx =
                    {
                        ChannelId = channelId
                        Currency = currency
                        Fee = MinerFee (fee.Satoshi, DateTime.UtcNow, currency)
                        Tx =
                            {
                                NBitcoinTx = transactionBuilder.BuildTransaction true
                            }
                    }
                return Ok recoveryTransaction
        }

    member self.UpdateFee (channelId: ChannelIdentifier)
                          (feeRate: decimal)
                          (nonionIntroductionPoint: Option<NOnionEndPoint>)
                              : Async<Result<unit, IErrorMsg>> = async {
        let feeRatePerKw = FeeEstimator.FeeRateFromDecimal feeRate
        let nodeMasterPrivKey =
            match self with
            | Client nodeClient -> nodeClient.NodeMasterPrivKey
            | Server nodeServer -> nodeServer.NodeMasterPrivKey
        let! activeChannelRes =
            ActiveChannel.ConnectReestablish
                self.ChannelStore
                nodeMasterPrivKey
                channelId
                nonionIntroductionPoint
        match activeChannelRes with
        | Error reconnectActiveChannelError ->
            if reconnectActiveChannelError.PossibleBug then
                let msg =
                    SPrintF2
                        "error connecting to peer to update fee %s: %s"
                        (channelId.ToString())
                        (reconnectActiveChannelError :> IErrorMsg).Message
                Infrastructure.ReportWarningMessage msg
                |> ignore<bool>
            return Error (NodeUpdateFeeError.Reconnect reconnectActiveChannelError :> IErrorMsg)
        | Ok activeChannel ->
            let! activeChannelAfterUpdateFeeRes = activeChannel.UpdateFee feeRatePerKw
            match activeChannelAfterUpdateFeeRes with
            | Error updateFeeError ->
                return Error (NodeUpdateFeeError.UpdateFee updateFeeError :> IErrorMsg)
            | Ok activeChannelAfterUpdateFee ->
                (activeChannelAfterUpdateFee :> IDisposable).Dispose()
                return Ok ()
    }

    member self.CheckForChannelFraudAndSendRevocationTx (channelId: ChannelIdentifier): Async<Option<string>> =
        ChainWatcher.CheckForChannelFraudAndSendRevocationTx channelId
                                                             self.ChannelStore

module public Connection =
    let public StartClient (channelStore: ChannelStore)
                           (password: string)
                               : NodeClient =
        let privateKey = Account.GetPrivateKey channelStore.Account password
        let nodeMasterPrivKey: NodeMasterPrivKey =
            NodeClient.AccountPrivateKeyToNodeSecret privateKey
            |> NodeMasterPrivKey
        NodeClient (channelStore, nodeMasterPrivKey)

    let public StartServer (channelStore: ChannelStore)
                           (password: string)
                           (nodeServerType: NodeServerType)
                               : Async<NodeServer> = async {
        let privateKey = Account.GetPrivateKey channelStore.Account password
        let nodeMasterPrivKey: NodeMasterPrivKey =
            NodeClient.AccountPrivateKeyToNodeSecret privateKey
            |> NodeMasterPrivKey
        let! transportListener = TransportListener.Bind nodeMasterPrivKey nodeServerType
        return new NodeServer (channelStore, transportListener)
    }

