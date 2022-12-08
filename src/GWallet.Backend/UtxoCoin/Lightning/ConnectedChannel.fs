namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.Net

open NBitcoin
open DotNetLightning.Channel
open DotNetLightning.Channel.ChannelSyncing
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
    | ChannelError of ChannelError
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
            | ChannelError channelError ->
                SPrintF1 "Channel error: %s" channelError.Message
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
            | ChannelError channelError -> channelError.RecommendedAction <> ChannelConsumerAction.Ignore
            | WrongChannelId _ -> false
            | OutOfSync -> false
            | WrongDataLossProtect -> true

    member internal self.PossibleBug =
        match self with
        | RecvReestablish err -> err.PossibleBug
        | PeerErrorResponse _
        | ExpectedReestablishMsg _
        | ExpectedReestablishOrFundingLockedMsg _ -> false
        | ChannelError _ -> false
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
                                      (channelStore: ChannelStore)
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
                    let channelSyncResult = channel.Channel.ApplyChannelReestablish theirReestablishMsg
                    let channelAfterApplyReestablish = { channel with Channel = channelSyncResult.Channel }
                    match channelSyncResult.SyncResult with
                    | SyncResult.Success [] ->
                        return Ok(peerNodeAfterReestablishReceived, channelAfterApplyReestablish)
                    | SyncResult.Success messages ->
                        let rec sendMessages (peerNode: PeerNode) remainingMessages =
                            async {
                                match remainingMessages with
                                | head :: tail -> 
                                    let! newPeerNode = peerNode.SendMsg head 
                                    return! sendMessages newPeerNode tail
                                | [] -> return peerNode
                            }
                        let! peerNodeAfterMessagesSent = sendMessages peerNodeAfterReestablishReceived messages

                        // Wait for reply and process a reply after sending messages
                        // possible messages: IUpdateMsg, RevokeAndACKMsg, CommitmentSignedMsg
                        let processReply (channel: Channel) (peerNode: PeerNode) : Async<Result<Channel * PeerNode, ReestablishError>> =
                            async {
                                let! nextMsg = peerNode.RecvChannelMsg()
                                match nextMsg with
                                | Ok(newPeerNode, (:? UpdateAddHTLCMsg as updateAddHtlcMsg)) -> 
                                    let! blockHeightResponse =
                                            Server.Query Currency.BTC
                                                (QuerySettings.Default ServerSelectionMode.Fast)
                                                (ElectrumClient.SubscribeHeaders ())
                                                None
                                    let blockHeight = (blockHeightResponse.Height |> uint32) |> BlockHeight
                                    return 
                                        match channel.ApplyUpdateAddHTLC updateAddHtlcMsg blockHeight with
                                        | Ok chan -> Ok(chan, newPeerNode)
                                        | Error channelError -> Error <| ReestablishError.ChannelError channelError
                                | Ok(newPeerNode, (:? UpdateFailHTLCMsg as updateFailHtlcMsg)) -> 
                                    return
                                        match channel.ApplyUpdateFailHTLC updateFailHtlcMsg with
                                        | Ok chan -> Ok(chan, newPeerNode)
                                        | Error channelError -> Error <| ReestablishError.ChannelError channelError
                                | Ok(newPeerNode, (:? UpdateFailMalformedHTLCMsg as updateFailMalformedHtlcMsg)) ->
                                    return 
                                        match channel.ApplyUpdateFailMalformedHTLC updateFailMalformedHtlcMsg with
                                        | Ok chan -> Ok(chan, newPeerNode)
                                        | Error channelError -> Error <| ReestablishError.ChannelError channelError
                                | Ok(newPeerNode, (:? UpdateFulfillHTLCMsg as updateFulfillHtlcMsg)) ->
                                    return
                                        match channel.ApplyUpdateFulfillHTLC updateFulfillHtlcMsg with
                                        | Ok chan -> Ok(chan, newPeerNode)
                                        | Error channelError -> Error <| ReestablishError.ChannelError channelError
                                | Ok(newPeerNode, (:? RevokeAndACKMsg as revokeAndAckMsg)) ->
                                    return
                                        match channel.ApplyRevokeAndACK revokeAndAckMsg with
                                        | Ok chan -> Ok(chan, newPeerNode)
                                        | Error channelError -> Error <| ReestablishError.ChannelError channelError
                                | Ok(newPeerNode, (:? CommitmentSignedMsg as commitmentSignedMsg)) ->
                                    match channel.ApplyCommitmentSigned commitmentSignedMsg with
                                    | Ok(chan, revokeAndAckMsg) -> 
                                        let! newPeerNode = newPeerNode.SendMsg revokeAndAckMsg
                                        return Ok(chan, newPeerNode)
                                    | Error channelError -> return Error <| ReestablishError.ChannelError channelError
                                | Error recvChannelMsgError -> 
                                    return
                                        match recvChannelMsgError with
                                        | RecvChannelMsgError.RecvMsg recvMsg -> 
                                            Error <| ReestablishError.RecvReestablish recvMsg
                                        | RecvChannelMsgError.ReceivedPeerErrorMessage(newPeerNode, peerErrorMsg) -> 
                                            Error <| ReestablishError.PeerErrorResponse(newPeerNode, peerErrorMsg)
                                | _ -> return failwith "not expected"
                            }
                        
                        let timeout = 1000 // in ms
                        let rec processReplies (channel: Channel) (peerNode: PeerNode) =
                            async {
                                let! reply = processReply channel peerNode |> Async.withTimeout timeout
                                match reply with
                                | Some(Ok(newChannel, newPeerNode)) -> 
                                    return! processReplies newChannel newPeerNode
                                | Some(Error _ as err) -> return err
                                | None -> return Ok(channel, peerNode)
                            }

                        let! processRepliesResult = processReplies channelAfterApplyReestablish.Channel peerNodeAfterMessagesSent
                        match processRepliesResult with
                        | Ok(chan, peerNode) -> return Ok(peerNode, { channelAfterApplyReestablish with Channel = chan })
                        | Error err -> return Error err
                    | SyncResult.LocalLateProven _ ->
                        Infrastructure.LogError("Sync error: " + channelSyncResult.ErrorMessage)
                        do! 
                            peerNodeAfterReestablishReceived.SendError 
                                "sync error - we were using outdated commitment" 
                                (Some channelId.DnlChannelId)
                            |> Async.Ignore
                        // SHOULD store my_current_per_commitment_point to retrieve funds 
                        // should the sending node broadcast its commitment transaction on-chain
                        let serializedChannel = channelStore.LoadChannel channelId
                        let updatedSerializedChannel = 
                            { serializedChannel with SavedChannelState = channelAfterApplyReestablish.Channel.SavedChannelState }
                        channelStore.SaveChannel updatedSerializedChannel

                        return Error <| OutOfSync
                    | SyncResult.LocalLateUnproven _ ->
                        Infrastructure.LogError("Sync error: " + channelSyncResult.ErrorMessage)
                        do! 
                            peerNodeAfterReestablishReceived.SendError 
                                "sync error - we may be using outdated commitment" 
                                (Some channelId.DnlChannelId) 
                            |> Async.Ignore
                        return Error <| OutOfSync
                    | SyncResult.RemoteLate ->
                        Infrastructure.LogError("Sync error: " + channelSyncResult.ErrorMessage)
                        do! 
                            peerNodeAfterReestablishReceived.SendError 
                                "sync error - you are using outdated commitment" 
                                (Some channelId.DnlChannelId) 
                            |> Async.Ignore
                        return Error <| OutOfSync
                    | SyncResult.RemoteLying _ ->
                        Infrastructure.LogError("Sync error: " + channelSyncResult.ErrorMessage)
                        do! 
                            peerNodeAfterReestablishReceived.SendError 
                                "sync error - you provided incorrect data in data_loss_protect" 
                                (Some channelId.DnlChannelId) 
                            |> Async.Ignore
                        return Error <| WrongDataLossProtect
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
                ConnectedChannel.Reestablish peerNode channel channelStore
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
                ConnectedChannel.Reestablish peerNode channel channelStore
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

