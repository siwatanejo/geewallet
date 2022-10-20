namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.Net

open NBitcoin
open DotNetLightning.Channel
open DotNetLightning.Utils
open DotNetLightning.Serialization.Msgs
open ResultUtils.Portability

open GWallet.Backend
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks
open GWallet.Backend.UtxoCoin


type internal ReestablishError =
    | RecvReestablish of RecvMsgError
    | PeerErrorResponse of PeerNode * PeerErrorMessage
    | ExpectedReestablishMsg of ILightningMsg
    | ExpectedReestablishOrFundingLockedMsg of ILightningMsg
    | WrongChannelId of given: ChannelId * expected: ChannelId
    | OutOfSync
    | WrongDataLossProtect
    interface IErrorMsg with
        member self.Message =
            match self with
            | RecvReestablish err ->
                SPrintF1 "Error receiving channel_reestablish: %s" (err :> IErrorMsg).Message
            | PeerErrorResponse (_, err) ->
                SPrintF1 "Peer responded to our channel_reestablish with an error: %s" (err :> IErrorMsg).Message
            | ExpectedReestablishMsg msg ->
                SPrintF1 "Expected channel_reestablish, got %A" (msg.GetType())
            | ExpectedReestablishOrFundingLockedMsg msg ->
                SPrintF1 "Expected channel_reestablish or funding_locked, got %A" (msg.GetType())
            | WrongChannelId (given, expected) ->
                SPrintF2 "Wrong channel_id. Expected: %A, given: %A" expected.Value given.Value
            | OutOfSync ->
                "Channel is out of sync"
            | WrongDataLossProtect -> 
                "Wrong data loss protect values"
        member self.ChannelBreakdown: bool =
            match self with
            | RecvReestablish recvMsgError ->
                (recvMsgError :> IErrorMsg).ChannelBreakdown
            | PeerErrorResponse _ -> true
            | ExpectedReestablishMsg _ -> false
            | ExpectedReestablishOrFundingLockedMsg _ -> false
            | WrongChannelId _ -> false
            | OutOfSync -> false
            | WrongDataLossProtect -> true

    member internal self.PossibleBug =
        match self with
        | RecvReestablish err -> err.PossibleBug
        | PeerErrorResponse _
        | ExpectedReestablishMsg _
        | ExpectedReestablishOrFundingLockedMsg _ -> false
        | OutOfSync -> false
        | WrongChannelId _ -> false
        | WrongDataLossProtect -> false

type internal ReconnectError =
    | Connect of ConnectError
    | Reestablish of ReestablishError
    interface IErrorMsg with
        member self.Message =
            match self with
            | Connect err ->
                SPrintF1 "Error reconnecting to peer: %s" (err :> IErrorMsg).Message
            | Reestablish err ->
                SPrintF1 "Error reestablishing channel with connected peer: %s" (err :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | Connect connectError ->
                (connectError :> IErrorMsg).ChannelBreakdown
            | Reestablish reestablishError ->
                (reestablishError :> IErrorMsg).ChannelBreakdown

    member internal self.PossibleBug =
        match self with
        | Connect err -> err.PossibleBug
        | Reestablish err -> err.PossibleBug

type internal ConnectedChannel =
    {
        PeerNode: PeerNode
        Channel: MonoHopUnidirectionalChannel
        Account: NormalUtxoAccount
        MinimumDepth: BlockHeightOffset32
        ChannelIndex: int
        ClosingTimestampUtc: Option<DateTime>
    }
    interface IDisposable with
        member self.Dispose() =
            (self.PeerNode :> IDisposable).Dispose()

    static member private LoadChannel (channelStore: ChannelStore)
                                      (nodeMasterPrivKey: NodeMasterPrivKey)
                                      (channelId: ChannelIdentifier)
                                          : Async<SerializedChannel * MonoHopUnidirectionalChannel> = async {
        let serializedChannel = channelStore.LoadChannel channelId
        Infrastructure.LogDebug <| SPrintF1 "loading channel for %s" (channelId.ToString())
        let! channel =
            MonoHopUnidirectionalChannel.Create
                channelStore.Account
                nodeMasterPrivKey
                serializedChannel.ChannelIndex
                serializedChannel.SavedChannelState
                serializedChannel.RemoteNextCommitInfo
                serializedChannel.NegotiatingState
                serializedChannel.Commitments
        return serializedChannel, channel
    }

    static member private Reestablish (peerNode: PeerNode)
                                      (channel: MonoHopUnidirectionalChannel)
                                          : Async<Result<PeerNode * MonoHopUnidirectionalChannel, ReestablishError>> = async {
        let channelId = channel.ChannelId
        let ourReestablishMsg = channel.Channel.CreateChannelReestablish()
        Infrastructure.LogDebug <| SPrintF1 "sending reestablish for %s" (channelId.ToString())
        let! peerNodeAfterReestablishSent = peerNode.SendMsg ourReestablishMsg
        Infrastructure.LogDebug <| SPrintF1 "receiving reestablish for %s" (channelId.ToString())
        let! reestablishRes = async {
            let! recvMsgRes = peerNodeAfterReestablishSent.RecvChannelMsg()
            match recvMsgRes with
            | Error (RecvMsg recvMsgError) -> return Error <| RecvReestablish recvMsgError
            | Error (ReceivedPeerErrorMessage (peerNodeAfterNextMsgReceived, errorMessage)) ->
                return Error <| PeerErrorResponse (peerNodeAfterNextMsgReceived, errorMessage)
            | Ok (peerNodeAfterNextMsgReceived, channelMsg) ->
                match channelMsg with
                | :? ChannelReestablishMsg as reestablishMsg ->
                    return Ok (peerNodeAfterNextMsgReceived, reestablishMsg)
                | :? FundingLockedMsg ->
                    let! recvMsgRes = peerNodeAfterNextMsgReceived.RecvChannelMsg()
                    match recvMsgRes with
                    | Error (RecvMsg recvMsgError) -> return Error <| RecvReestablish recvMsgError
                    | Error (ReceivedPeerErrorMessage (peerNodeAfterReestablishReceived, errorMessage)) ->
                        return Error <| PeerErrorResponse
                            (peerNodeAfterReestablishReceived, errorMessage)
                    | Ok (peerNodeAfterReestablishReceived, channelMsg) ->
                        match channelMsg with
                        | :? ChannelReestablishMsg as reestablishMsg ->
                            return Ok (peerNodeAfterReestablishReceived, reestablishMsg)
                        | msg ->
                            return Error <| ExpectedReestablishMsg msg
                | msg ->
                    return Error <| ExpectedReestablishOrFundingLockedMsg msg
        }
        match reestablishRes with
        | Error err -> return Error err
        | Ok (peerNodeAfterReestablishReceived, theirReestablishMsg) ->
            // Check their reestablish msg
            // See https://github.com/lightning/bolts/blob/master/02-peer-protocol.md#message-retransmission
            if theirReestablishMsg.ChannelId <> channelId.DnlChannelId then
                return Error <| WrongChannelId(theirReestablishMsg.ChannelId, channelId.DnlChannelId)
            else
                if ourReestablishMsg.NextCommitmentNumber = CommitmentNumber.FirstCommitment.NextCommitment()
                    && theirReestablishMsg.NextCommitmentNumber = CommitmentNumber.FirstCommitment.NextCommitment() then
                    // ?
                    return Ok(peerNodeAfterReestablishReceived, channel)
                else
                    match channel.Channel.ApplyChannelReestablish theirReestablishMsg with
                    | Ok(messages, channelAfterApplyReestablish) ->
                        let channel = { channel with Channel = channelAfterApplyReestablish }
                        let rec sendMessages (peerNode: PeerNode) remainingMessages =
                            async {
                                match remainingMessages with
                                | head :: tail -> 
                                    let! newPeerNode = peerNode.SendMsg head 
                                    return! sendMessages newPeerNode tail
                                | [] -> return peerNode
                            }
                        let! peerNodeAfterMessagesSent = sendMessages peerNodeAfterReestablishReceived messages
                        return Ok(peerNodeAfterMessagesSent, channel)
                    | Error(ChannelError.OutOfSync _msg) ->
                        return Error <| OutOfSync
                    | Error(ChannelError.OutOfSyncLocalLateProven(_msg, _currentPerCommitmentPoint)) ->
                        // SHOULD store my_current_per_commitment_point to retrieve funds 
                        // should the sending node broadcast its commitment transaction on-chain
                        // -- where to store it?
                        return Error <| OutOfSync
                    | Error(ChannelError.OutOfSyncRemoteLying _msg) ->
                        return Error <| WrongDataLossProtect
                    | Error _ -> return failwith "unreachable"
    }

    static member internal ConnectFromWallet (channelStore: ChannelStore)
                                             (nodeMasterPrivKey: NodeMasterPrivKey)
                                             (channelId: ChannelIdentifier)
                                             (nonionEndPoint: Option<NOnionEndPoint>)
                                                 : Async<Result<ConnectedChannel, ReconnectError>> = async {
        let! serializedChannel, channel =
            ConnectedChannel.LoadChannel channelStore nodeMasterPrivKey channelId
        let! connectRes =
            let nodeId = channel.RemoteNodeId
            let nodeIdentifier =
                match nonionEndPoint with
                | Some introPoint ->
                    NodeIdentifier.TorEndPoint introPoint
                | None ->
                    match serializedChannel.NodeTransportType with
                    | NodeTransportType.Client (NodeClientType.Tcp counterPartyIP) ->
                        NodeIdentifier.TcpEndPoint
                            {
                                NodeEndPoint.NodeId = PublicKey nodeId.Value
                                IPEndPoint = counterPartyIP
                            }
                    | _ ->
                        failwith "Unreachable because channel's user is fundee and not the funder"
            PeerNode.Connect
                nodeMasterPrivKey
                nodeIdentifier
                channelStore.Currency
                (serializedChannel.Capacity())
                ConnectionPurpose.ChannelOpening
        match connectRes with
        | Error connectError -> return Error <| Connect connectError
        | Ok peerNode ->
            let! reestablishRes =
                ConnectedChannel.Reestablish peerNode channel
            match reestablishRes with
            | Error reestablishError -> return Error <| Reestablish reestablishError
            | Ok (peerNodeAfterReestablish, channelAfterReestablish) ->
                let minimumDepth = serializedChannel.MinDepth()
                let channelIndex = serializedChannel.ChannelIndex
                let connectedChannel = {
                    Account = channelStore.Account
                    Channel = channelAfterReestablish
                    PeerNode = peerNodeAfterReestablish
                    MinimumDepth = minimumDepth
                    ChannelIndex = channelIndex
                    ClosingTimestampUtc = serializedChannel.ClosingTimestampUtc
                }
                return Ok connectedChannel
    }

    static member internal AcceptFromWallet (channelStore: ChannelStore)
                                            (transportListener: TransportListener)
                                            (channelId: ChannelIdentifier)
                                                : Async<Result<ConnectedChannel, ReconnectError>> = async {
        let! serializedChannel, channel =
            ConnectedChannel.LoadChannel channelStore transportListener.NodeMasterPrivKey channelId
        let! connectRes =
            let nodeId = channel.RemoteNodeId
            PeerNode.AcceptFromTransportListener
                transportListener
                nodeId
                channelStore.Currency
                None
        match connectRes with
        | Error connectError -> return Error <| Connect connectError
        | Ok peerNode ->
            let! reestablishRes =
                ConnectedChannel.Reestablish peerNode channel
            match reestablishRes with
            | Error reestablishError -> return Error <| Reestablish reestablishError
            | Ok (peerNodeAfterReestablish, channelAfterReestablish) ->
                let minimumDepth = serializedChannel.MinDepth()
                let channelIndex = serializedChannel.ChannelIndex
                let connectedChannel = {
                    Account = channelStore.Account
                    Channel = channelAfterReestablish
                    PeerNode = peerNodeAfterReestablish
                    MinimumDepth = minimumDepth
                    ChannelIndex = channelIndex
                    ClosingTimestampUtc = serializedChannel.ClosingTimestampUtc
                }
                return Ok connectedChannel
    }

    member self.SaveToWallet() =
        let channelStore = ChannelStore self.Account

        let serializedChannel : SerializedChannel = {
            ChannelIndex = self.ChannelIndex
            RemoteNextCommitInfo = self.Channel.Channel.RemoteNextCommitInfo
            SavedChannelState = self.Channel.Channel.SavedChannelState
            NegotiatingState = self.Channel.Channel.NegotiatingState
            Commitments = self.Channel.Channel.Commitments
            AccountFileName = self.Account.AccountFile.Name
            ForceCloseTxIdOpt = None            
            LocalChannelPubKeys = self.Channel.ChannelPrivKeys.ToChannelPubKeys()
            NodeTransportType = self.PeerNode.NodeTransportType
            MainBalanceRecoveryStatus = Unresolved
            ClosingTimestampUtc = self.ClosingTimestampUtc
            HtlcDelayedTxs = List.empty
            BroadcastedHtlcRecoveryTxs = List.empty
            BroadcastedHtlcTxs = List.empty
        }
        channelStore.SaveChannel serializedChannel

    member internal self.RemoteNodeId
        with get(): NodeId = self.Channel.RemoteNodeId

    member internal self.Network
        with get(): Network = self.Channel.Network

    member self.ChannelId
        with get(): ChannelIdentifier =
            self.Channel.ChannelId

    member self.FundingTxId
        with get(): TransactionIdentifier =
            self.Channel.FundingTxId

    member internal self.FundingScriptCoin
        with get(): ScriptCoin =
            self.Channel.FundingScriptCoin

    member self.SendError (err: string): Async<ConnectedChannel> = async {
        let! peerNode = self.PeerNode.SendError err (self.Channel.Channel.SavedChannelState.StaticChannelConfig.ChannelId() |> Some)
        return {
            self with
                PeerNode = peerNode
        }
    }

