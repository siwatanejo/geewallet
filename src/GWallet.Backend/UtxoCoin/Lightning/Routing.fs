namespace GWallet.Backend.UtxoCoin.Lightning

open System

open GWallet.Backend
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks
open ResultUtils.Portability

open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Serialization.Msgs
open QuikGraph



type RoutingGrpahEdge = 
    {
        Source : NodeId
        Target : NodeId
        Update: UnsignedChannelUpdateMsg
    }
    with
        interface IEdge<NodeId> with
            member self.Source = self.Source
            member self.Target = self.Target

        member self.ShortChannelId = self.Update.ShortChannelId

type RoutingGraph = QuikGraph.ArrayAdjacencyGraph<NodeId, RoutingGrpahEdge>


module Routing =
    exception RoutingQueryException of string

    type private ChannelUpdates =
        {
            Forward: UnsignedChannelUpdateMsg option
            Backward: UnsignedChannelUpdateMsg option
        }
        with
            static member Empty = { Forward = None; Backward = None }

            member self.With(update: UnsignedChannelUpdateMsg) =
                let isForward = (update.ChannelFlags &&& 1uy) = 0uy
                if isForward then
                    match self.Forward with
                    | Some(prevUpd) when update.Timestamp < prevUpd.Timestamp -> self
                    | _ -> { self with Forward = Some(update) }
                else
                    match self.Backward with
                    | Some(prevUpd) when update.Timestamp < prevUpd.Timestamp -> self
                    | _ -> { self with Backward = Some(update) }
    

    let mutable private graph : RoutingGraph option = None


    let internal QueryRoutingGossip (currency: Currency) (nodeIdentifier: NodeIdentifier) : Async<seq<IRoutingMsg>> =
        async {
            let firstBlocknum = 0u
            let numberOfBlocks = 0xffffffffu
            let currency = currency
            let chainHash = 
                match currency with
                | BTC -> Network.Main.GenesisHash
                | _ -> failwith <| SPrintF1 "Unsupported currency: %A" currency
            let queryMsg = 
                { 
                    QueryChannelRangeMsg.ChainHash=chainHash
                    FirstBlockNum=BlockHeight(firstBlocknum)
                    NumberOfBlocks=numberOfBlocks
                    TLVs=[||]
                }

            try
                let! initialNode = 
                    let throwawayPrivKey = NodeMasterPrivKey.NodeMasterPrivKey(ExtKey())
                    let purpose = ConnectionPurpose.Routing
                    PeerNode.Connect throwawayPrivKey nodeIdentifier currency Money.Zero purpose
                
                // step 1: send query_channel_range, read all replies and collect short channel ids from them
                let! initialNode = 
                    match initialNode with
                    | Ok(node) -> node.SendMsg queryMsg
                    | Error(e) -> raise (RoutingQueryException <| e.ToString())
            
                let shortChannelIds = ResizeArray<ShortChannelId>()

                let rec queryShortChannelIds (node: PeerNode) : Async<PeerNode> =
                    async {
                        let! response = node.MsgStream.RecvMsg()
                        match response with
                        | Error(e) -> 
                            return raise (RoutingQueryException <| e.ToString())
                        | Ok(newState, (:? ReplyChannelRangeMsg as replyChannelRange)) -> 
                            let node = { node with MsgStream = newState }
                            shortChannelIds.AddRange replyChannelRange.ShortIds
                            if replyChannelRange.Complete then
                                return node
                            else
                                return! queryShortChannelIds node
                        | Ok(newState, msg) -> 
                            // ignore all other messages
                            let logMsg = 
                                SPrintF1 "Received unexpected message while processing reply_channel_range messages:\n %A" msg
                            Infrastructure.LogDebug logMsg
                            return! queryShortChannelIds { node with MsgStream = newState }
                    }

                let! node = queryShortChannelIds initialNode

                let batchSize = 1000
                let batches = shortChannelIds |> Seq.chunkBySize batchSize |> Collections.Generic.Queue
                let results = ResizeArray<IRoutingMsg>()

                // step 2: split shortChannelIds into batches and for each batch:
                // - send query_short_channel_ids
                // - receive routing messages and add them to result until we get reply_short_channel_ids_end
                let rec processMessages (node: PeerNode) : Async<PeerNode> =
                    async {
                        let! response = node.MsgStream.RecvMsg()
                        match response with
                        | Error(e) -> 
                            return raise (RoutingQueryException <| e.ToString())
                        | Ok(newState, (:? IRoutingMsg as msg)) -> 
                            let node = { node with MsgStream = newState }
                            match msg with
                            | :? ReplyShortChannelIdsEndMsg as _channelIdsEnd -> 
                                if batches.Count = 0 then
                                    return node // end processing
                                else
                                    return! sendNextBatch node
                            | _ ->
                                results.Add msg
                                return! processMessages node
                        | Ok(newState, msg) -> 
                            // ignore all other messages
                            let logMsg = 
                                SPrintF1 "Received unexpected message while processing routing messages:\n %A" msg
                            Infrastructure.LogDebug logMsg
                            return! processMessages { node with MsgStream = newState }
                    }
                and sendNextBatch (node: PeerNode) : Async<PeerNode> =
                    async {
                        let queryShortIdsMsg =
                            {
                                QueryShortChannelIdsMsg.ChainHash=chainHash
                                ShortIdsEncodingType=EncodingType.SortedPlain
                                ShortIds=batches.Dequeue()
                                TLVs=[||]
                            }
                        let! node = node.SendMsg queryShortIdsMsg
                        return! processMessages node
                    }

                do! sendNextBatch node |> Async.Ignore
            
                return (results :> seq<_>)
            with
            | :? RoutingQueryException as _exn ->
                return Seq.empty
        }

    let internal CreateRoutingGraph (currency: Currency) (nodeIdentifier: NodeIdentifier) : Async<RoutingGraph> =
        async {
            let! gossipMessages = QueryRoutingGossip currency nodeIdentifier
            let announcements = Collections.Generic.HashSet<UnsignedChannelAnnouncementMsg>()
            let updates = Collections.Generic.Dictionary<ShortChannelId, ChannelUpdates>()

            for message in gossipMessages do
                match message with
                | :? ChannelAnnouncementMsg as channelAnnouncement ->
                    announcements.Add channelAnnouncement.Contents
                    |> ignore
                | :? ChannelUpdateMsg as channelUpdate ->
                    let upd = channelUpdate.Contents
                    match updates.TryGetValue upd.ShortChannelId with
                    | true, storedUpdates -> 
                        updates.[upd.ShortChannelId] <- storedUpdates.With upd
                    | _ -> 
                        updates.[upd.ShortChannelId] <- ChannelUpdates.Empty.With upd
                | :? NodeAnnouncementMsg as _msg ->
                    ()
                | _ -> 
                    () // ignore gossip queries from peer
        
            announcements.RemoveWhere(fun ann -> not(updates.ContainsKey ann.ShortChannelId)) |> ignore
            let baseGraph = QuikGraph.AdjacencyGraph<NodeId, RoutingGrpahEdge>()

            for ann in announcements do
                let updates = updates.[ann.ShortChannelId]
                
                let addEdge source traget (upd : UnsignedChannelUpdateMsg) =
                    let edge = { Source=source; Target=traget; Update=upd }
                    baseGraph.AddVerticesAndEdge edge |> ignore
                
                updates.Forward |> Option.iter (addEdge ann.NodeId1 ann.NodeId2)
                updates.Backward |> Option.iter (addEdge ann.NodeId2 ann.NodeId1)
        
            return RoutingGraph(baseGraph)
        }

    let UpdateRoutingGraph currency =
        let node_id = 
            match currency with
            | Currency.BTC ->
                let addr = "033d8656219478701227199cbd6f670335c8d408a92ae88b962c49d4dc0e83e025@34.65.85.39:9735"
                    //"0279ff5e458ad89aa30b7e7092acdd30e9aeb470a02da4d6796af260faf31c90ac@127.0.0.1:9735"
                NodeIdentifier.TcpEndPoint(NodeEndPoint.Parse Currency.BTC addr)
            | currency -> failwith <| SPrintF1 "Currency not supported: %A" currency
        async {
            // TODO: avoid concurrent updates
            match graph with
            | Some(_routingGraph) -> 
                () // TODO: update
            | None ->
                let! newGraph = CreateRoutingGraph currency node_id
                graph <- Some newGraph
                ()
        }
