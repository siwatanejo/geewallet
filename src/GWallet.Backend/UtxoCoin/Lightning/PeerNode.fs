namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.Net

open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Serialization.Msgs
open ResultUtils.Portability

open GWallet.Backend
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks


type internal RecvChannelMsgError =
    | RecvMsg of RecvMsgError
    | ReceivedPeerErrorMessage of PeerNode * PeerErrorMessage
    interface IErrorMsg with
        member this.Message =
            match this with
            | RecvMsg err ->
                SPrintF1 "Error receiving message from peer: %s" (err :> IErrorMsg).Message
            | ReceivedPeerErrorMessage (_, err) ->
                SPrintF1 "Error message from peer: %s" (err :> IErrorMsg).Message
        member self.ChannelBreakdown: bool =
            match self with
            | RecvMsg recvMsgError -> (recvMsgError :> IErrorMsg).ChannelBreakdown
            | ReceivedPeerErrorMessage _ -> true

and internal PeerNode =
    {
        InitMsg: InitMsg
        MsgStream: MsgStream
        NodeTransportType: NodeTransportType
    }
    interface IDisposable with
        member self.Dispose() =
            (self.MsgStream :> IDisposable).Dispose()

    static member internal Connect (nodeMasterPrivKey: NodeMasterPrivKey)
                                   (nodeIndentifier: NodeIdentifier)
                                   (currency: Currency)
                                   (fundingAmount: Money)
                                       : Async<Result<PeerNode, ConnectError>> = async {
        let! connectRes = MsgStream.Connect nodeMasterPrivKey nodeIndentifier currency fundingAmount
        match connectRes with
        | Error connectError -> return Error connectError
        | Ok (initMsg, msgStream) ->
            let nodeClientType =
                match nodeIndentifier with
                | NodeIdentifier.TorEndPoint _ ->
                    NodeClientType.Tor
                | NodeIdentifier.TcpEndPoint nodeEndPoint ->
                    NodeClientType.Tcp nodeEndPoint.IPEndPoint
            return Ok {
                InitMsg = initMsg
                MsgStream = msgStream
                NodeTransportType = NodeTransportType.Client nodeClientType
            }
    }


    static member internal AcceptFromTransportListener (transportListener: TransportListener)
                                                       (peerNodeId: NodeId)
                                                       (currency: Currency)
                                                       (fundingAmountOpt: Option<Money>)
                                                           : Async<Result<PeerNode, ConnectError>> = async {
        let! acceptRes = MsgStream.AcceptFromTransportListener transportListener currency fundingAmountOpt
        match acceptRes with
        | Error connectError -> return Error connectError
        | Ok (initMsg, msgStream) ->
            if msgStream.RemoteNodeId = peerNodeId then
                return Ok {
                    InitMsg = initMsg
                    MsgStream = msgStream
                    NodeTransportType = NodeTransportType.Server transportListener.NodeServerType
                }
            else
                (msgStream :> IDisposable).Dispose()
                return! PeerNode.AcceptFromTransportListener transportListener peerNodeId currency fundingAmountOpt
    }

    static member internal AcceptAnyFromTransportListener (transportListener: TransportListener)
                                                          (currency: Currency)
                                                          (fundingAmountOpt: Option<Money>)
                                                              : Async<Result<PeerNode, ConnectError>> = async {
        let! acceptRes = MsgStream.AcceptFromTransportListener transportListener currency fundingAmountOpt
        match acceptRes with
        | Error connectError -> return Error connectError
        | Ok (initMsg, msgStream) ->
            return Ok {
                InitMsg = initMsg
                MsgStream = msgStream
                NodeTransportType = NodeTransportType.Server transportListener.NodeServerType
            }
    }

    member internal self.RemoteNodeId: NodeId =
        self.MsgStream.RemoteNodeId

    member internal self.RemoteEndPoint: Option<IPEndPoint> =
        self.MsgStream.RemoteEndPoint

    member internal self.NodeEndPoint: Option<NodeEndPoint> =
        self.MsgStream.NodeEndPoint

    member internal self.NodeMasterPrivKey(): NodeMasterPrivKey =
        self.MsgStream.NodeMasterPrivKey ()

    member internal self.SendMsg (msg: ILightningMsg): Async<PeerNode> = async {
        let! msgStream = self.MsgStream.SendMsg msg
        return { self with MsgStream = msgStream }
    }

    member internal self.SendError (err: string)
                                   (channelIdOpt: Option<ChannelId>)
                                       : Async<PeerNode> = async {
        let errorMsg = {
            ChannelId =
                match channelIdOpt with
                | Some channelId -> WhichChannel.SpecificChannel channelId
                | None -> WhichChannel.All
            Data = System.Text.Encoding.ASCII.GetBytes err
        }
        return! self.SendMsg errorMsg
    }

    member internal self.RecvChannelMsg(): Async<Result<PeerNode * IChannelMsg, RecvChannelMsgError>> =
        let rec recv (msgStream: MsgStream) = async {
            let! recvMsgRes = msgStream.RecvMsg()
            match recvMsgRes with
            | Error recvMsgError -> return Error <| RecvMsg recvMsgError
            | Ok (msgStreamAfterMsgReceived, msg) ->
                match msg with
                | :? ErrorMsg as errorMsg ->
                    let peerNode = { self with MsgStream = msgStreamAfterMsgReceived }
                    return Error <| ReceivedPeerErrorMessage (peerNode, { ErrorMsg = errorMsg })
                | :? PingMsg as pingMsg ->
                    let! msgStreamAfterPongSent = msgStreamAfterMsgReceived.SendMsg { PongMsg.BytesLen = pingMsg.PongLen }
                    return! recv msgStreamAfterPongSent
                | :? PongMsg ->
                    return failwith "sending pings is not implemented"
                | :? InitMsg ->
                    return failwith "unexpected init msg"
                | :? IRoutingMsg ->
                    Infrastructure.LogDebug "handling routing messages is not implemented"
                    return! recv msgStreamAfterMsgReceived
                | :? IChannelMsg as msg ->
                    let peerNode = { self with MsgStream = msgStreamAfterMsgReceived }
                    return Ok (peerNode, msg)
                | _ ->
                    return failwith <| SPrintF1 "unreachable %A" msg
        }
        recv self.MsgStream

    member internal self.RecvRoutingMsg(): Async<Result<PeerNode * IRoutingMsg, RecvChannelMsgError>> =
        let rec recv (msgStream: MsgStream) = async {
            let! recvMsgRes = msgStream.RecvMsg()
            match recvMsgRes with
            | Error recvMsgError -> return Error <| RecvMsg recvMsgError
            | Ok (msgStreamAfterMsgReceived, msg) ->
                match msg with
                | :? ErrorMsg as errorMsg ->
                    let peerNode = { self with MsgStream = msgStreamAfterMsgReceived }
                    return Error <| ReceivedPeerErrorMessage (peerNode, { ErrorMsg = errorMsg })
                | :? IRoutingMsg as msg ->
                    let peerNode = { self with MsgStream = msgStreamAfterMsgReceived }
                    return Ok (peerNode, msg)
                | _ ->
                    return failwith <| SPrintF1 "unreachable %A" msg
        }
        recv self.MsgStream

    static member internal QueryRoutingGossip (nodeMasterPrivKey: NodeMasterPrivKey)
                                              (nodeIndentifier: NodeIdentifier)
                                              (currency: Currency)
                                              (fundingAmount: Money)
                                              (firstTimestamp: uint32)
                                              (timestampRange: uint32) : Async<seq<IRoutingMsg>> =
        async {
            // non-hardcoded value?
            let chainHash = uint256.Parse "6fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000"
            let queryMsg = 
                { 
                    GossipTimestampFilterMsg.ChainHash=chainHash;
                    FirstTimestamp=firstTimestamp; 
                    TimestampRange=timestampRange 
                }
            match! PeerNode.Connect nodeMasterPrivKey nodeIndentifier currency fundingAmount with
            | Error(e) -> return failwith (e.ToString())
            | Ok(peerNode) ->
                let results = ResizeArray<IRoutingMsg>()
                let! peerNode = peerNode.SendMsg queryMsg 
                let mutable node = peerNode
                // How do we know when there is no more messages???
                let mutable hasMore = true
                while hasMore do
                    match! node.RecvRoutingMsg() with
                    | Error(e) -> return failwith (e.ToString())
                    | Ok(newState, msg) -> 
                        results.Add msg
                        node <- newState
                    hasMore <- false
                return (results :> seq<_>)
        }
