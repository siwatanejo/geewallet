namespace GWallet.Backend.UtxoCoin.Lightning

open System.IO
open System.Net.Http
open System.Linq

open Newtonsoft.Json
open NBitcoin
open DotNetLightning.Serialization
open DotNetLightning.Serialization.Msgs
open DotNetLightning.Utils
open ResultUtils.Portability

type MutableList<'T> = System.Collections.Generic.List<'T>

module RapidGossipSyncer =
    
    let private RGSPrefix = [| 76uy; 68uy; 75uy; 1uy |]

    type internal CompactAnnouncment =
        {
            ChannelFeatures: Result<FeatureBits, FeatureError>
            ShortChannelId: ShortChannelId
            NodeId1: NodeId
            NodeId2: NodeId
        }

    type internal CompactChannelUpdate =
        {
            ShortChannelId: ShortChannelId
            CLTVExpiryDelta: uint16
            HtlcMinimumMSat: uint64
            FeeBaseMSat: uint32
            FeeProportionalMillionths: uint32
            HtlcMaximumMSat: uint64
        }

    type internal ChannelUpdates =
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
    
    let private deserialize<'T when 'T: (new: unit -> 'T) and 'T :> ILightningSerializable<'T>> (stream: LightningReaderStream) =
        let inst = new 'T()
        inst.Deserialize stream
        inst

    type internal GossipData =
        {
            Announcements: CompactAnnouncment[]
            Updates: Map<ShortChannelId, ChannelUpdates>
        }
        with
            member self.Serialize(stream: System.IO.Stream) =
                use lightningStream = new DotNetLightning.Serialization.LightningWriterStream(stream)

                lightningStream.Write(self.Announcements.Length, false)
                for ann in self.Announcements do
                    self.SerializeAnnouncement(ann, lightningStream)
                
                lightningStream.Write(self.Updates.Count, false)
                for pair in self.Updates do
                    self.SerializeUpdatesPair(pair, lightningStream)

            member self.SerializeAnnouncement(ann, lightningStream) =
                lightningStream.Write(ann.ShortChannelId.ToUInt64(), false)
                lightningStream.Write ann.NodeId1.Value
                lightningStream.Write ann.NodeId2.Value
                match ann.ChannelFeatures with
                | Ok(features) -> 
                    lightningStream.Write 0uy
                    let byteArray = features.ToByteArray()
                    lightningStream.Write(uint8 byteArray.Length)
                    lightningStream.Write(byteArray)
                | Error(FeatureError.BogusFeatureDependency(_)) ->
                    lightningStream.Write 1uy
                | Error(FeatureError.UnknownRequiredFeature(_)) ->
                    lightningStream.Write 2uy

            member self.SerializeUpdatesPair(pair, lightningStream) =
                lightningStream.Write(pair.Key.ToUInt64(), false)
                let existenceFlags = 
                    (if pair.Value.Forward.IsSome then 0b01uy else 0uy) 
                    ||| (if pair.Value.Backward.IsSome then 0b10uy else 0uy)
                assert(existenceFlags <= 0b11uy)
                lightningStream.Write existenceFlags
                pair.Value.Forward |> Option.iter (fun upd -> (upd :> ILightningSerializable<_>).Serialize lightningStream)
                pair.Value.Backward |> Option.iter (fun upd -> (upd :> ILightningSerializable<_>).Serialize lightningStream)

            static member Deserialize(stream: System.IO.Stream) : GossipData =
                use lightningStream = new DotNetLightning.Serialization.LightningReaderStream(stream)
                
                let annCount = lightningStream.ReadInt32 false
                let announcements = 
                    Array.init
                        annCount
                        (fun _ -> GossipData.DeserializeAnnouncement lightningStream)
                                
                let updatesCount = lightningStream.ReadInt32 false
                let updatePairs =
                    Array.init
                        updatesCount
                        (fun _ -> GossipData.DeserializeUpdatePair lightningStream)

                {
                    Announcements = announcements
                    Updates = updatePairs |> Map.ofArray
                }

            static member DeserializeAnnouncement(lightningStream) = 
                let channelId = lightningStream.ReadUInt64(false)
                let nodeId1 = NodeId(lightningStream.ReadPubKey())
                let nodeId2 = NodeId(lightningStream.ReadPubKey())
                let featuresFlag = lightningStream.ReadByte()
                let featureBits =
                    match featuresFlag with
                    | 0uy -> 
                        let count = lightningStream.ReadByte()
                        let byteArray = lightningStream.ReadBytes(int count)
                        FeatureBits.TryCreate byteArray
                    | 1uy ->
                        Error(FeatureError.BogusFeatureDependency(""))
                    | 2uy ->
                        Error(FeatureError.UnknownRequiredFeature(""))
                    | _ -> 
                        failwith ("unexpected value: " + string(featuresFlag))
                {
                    ChannelFeatures = featureBits
                    ShortChannelId = channelId |> ShortChannelId.FromUInt64
                    NodeId1 = nodeId1
                    NodeId2 = nodeId2
                }

            static member DeserializeUpdatePair(lightningStream) = 
                let key = lightningStream.ReadUInt64(false) |> ShortChannelId.FromUInt64
                
                let existenceFlags = lightningStream.ReadByte()
                let value =
                    match existenceFlags with
                    | 0b00uy -> 
                        ChannelUpdates.Empty
                    | 0b01uy ->
                        { Forward= Some(deserialize<UnsignedChannelUpdateMsg> lightningStream); Backward = None }
                    | 0b10uy ->
                        { Forward = None; Backward = Some(deserialize<UnsignedChannelUpdateMsg> lightningStream) }
                    | 0b11uy ->
                        { Forward = Some(deserialize<UnsignedChannelUpdateMsg> lightningStream);
                          Backward = Some(deserialize<UnsignedChannelUpdateMsg> lightningStream) }
                    | _ -> failwith ("Unexpected value: " + (string existenceFlags))

                key, value

    /// see https://github.com/lightningdevkit/rust-lightning/tree/main/lightning-rapid-gossip-sync/#custom-channel-update
    module internal CustomChannelUpdateFlags =
        let Direction = 1uy
        let DisableCHannel = 2uy
        let HtlcMaximumMsat = 4uy
        let FeeProportionalMillionths = 8uy
        let FeeBaseMsat = 16uy
        let HtlcMinimumMsat = 32uy
        let CltvExpiryDelta = 64uy
        let IncrementalUpdate = 128uy

    let Sync () =
        async {
            use httpClient = new HttpClient()
            // Always do a full sync
            let! gossipData =
                httpClient.GetByteArrayAsync "https://rapidsync.lightningdevkit.org/snapshot/0"
                |> Async.AwaitTask

            use memStream = new MemoryStream(gossipData)
            use lightningReader = new LightningReaderStream(memStream)

            let prefix = Array.zeroCreate RGSPrefix.Length
            
            do! lightningReader.ReadAsync(prefix, 0, prefix.Length)
                |> Async.AwaitTask
                |> Async.Ignore

            if not (Enumerable.SequenceEqual (prefix, RGSPrefix)) then
                failwith "Invalid version prefix"

            let chainHash = lightningReader.ReadUInt256 true
            if chainHash <> Network.Main.GenesisHash then
                failwith "Invalid chain hash"

            let lastSeenTimestamp = lightningReader.ReadUInt32 false
            let backdatedTimestamp = lastSeenTimestamp - uint (24 * 3600 * 7)

            let nodeIdsCount = lightningReader.ReadUInt32 false
            let nodeIds = 
                Array.init
                    (int nodeIdsCount)
                    (fun _ -> lightningReader.ReadPubKey() |> NodeId)

            let announcementsCount = lightningReader.ReadUInt32 false

            let announcements = 
                [|
                    let mutable previousShortChannelId = 0UL
                    for _=1 to int announcementsCount do
                        let features = lightningReader.ReadWithLen () |> FeatureBits.TryCreate
                        let shortChannelId = previousShortChannelId + lightningReader.ReadBigSize ()
                        let nodeId1 = nodeIds.[lightningReader.ReadBigSize () |> int]
                        let nodeId2 = nodeIds.[lightningReader.ReadBigSize () |> int]

                        let compactAnn =
                            {
                                ChannelFeatures = features
                                ShortChannelId = shortChannelId |> ShortChannelId.FromUInt64
                                NodeId1 = nodeId1
                                NodeId2 = nodeId2
                            }
                        yield compactAnn
                        previousShortChannelId <- shortChannelId
                |]

            let updatesCount = lightningReader.ReadUInt32 false

            let defaultCltvExpiryDelta: uint16 = lightningReader.ReadUInt16 false
            let defaultHtlcMinimumMSat: uint64 = lightningReader.ReadUInt64 false
            let defaultFeeBaseMSat: uint32 = lightningReader.ReadUInt32 false
            let defaultFeeProportionalMillionths: uint32 = lightningReader.ReadUInt32 false
            let defaultHtlcMaximumMSat: uint64 = lightningReader.ReadUInt64 false

            let rec readUpdates (remainingCount: uint) (previousShortChannelId: uint64) (updates: Map<ShortChannelId, ChannelUpdates>) =
                if remainingCount = 0u then
                    updates
                else
                    let shortChannelId = previousShortChannelId + lightningReader.ReadBigSize ()
                    let customChannelFlag = lightningReader.ReadByte()
                    let standardChannelFlagMask = 0b0000_0011uy
                    let standardChannelFlag = customChannelFlag &&& standardChannelFlagMask

                    if customChannelFlag &&& CustomChannelUpdateFlags.IncrementalUpdate > 0uy then
                        failwith "We don't support increamental updates yet!"        

                    let cltvExpiryDelta =
                        if customChannelFlag &&& CustomChannelUpdateFlags.CltvExpiryDelta > 0uy then
                            lightningReader.ReadUInt16 false
                        else
                            defaultCltvExpiryDelta

                    let htlcMinimumMSat =
                        if customChannelFlag &&& CustomChannelUpdateFlags.HtlcMinimumMsat > 0uy then
                            lightningReader.ReadUInt64 false
                        else
                            defaultHtlcMinimumMSat

                    let feeBaseMSat =
                        if customChannelFlag &&& CustomChannelUpdateFlags.FeeBaseMsat > 0uy then
                            lightningReader.ReadUInt32 false
                        else
                            defaultFeeBaseMSat

                    let feeProportionalMillionths =
                        if customChannelFlag &&& CustomChannelUpdateFlags.FeeProportionalMillionths > 0uy then
                            lightningReader.ReadUInt32 false
                        else
                            defaultFeeProportionalMillionths
                    
                    let htlcMaximumMSat =
                        if customChannelFlag &&& CustomChannelUpdateFlags.HtlcMaximumMsat > 0uy then
                            lightningReader.ReadUInt64 false
                        else
                            defaultHtlcMaximumMSat

                    let structuredShortChannelId = shortChannelId |> ShortChannelId.FromUInt64

                    let channelUpdate =
                        {
                            UnsignedChannelUpdateMsg.ShortChannelId = structuredShortChannelId
                            Timestamp = backdatedTimestamp
                            ChainHash = Network.Main.GenesisHash
                            ChannelFlags = standardChannelFlag
                            MessageFlags = 1uy
                            CLTVExpiryDelta = cltvExpiryDelta |> BlockHeightOffset16
                            HTLCMinimumMSat = htlcMinimumMSat |> LNMoney.MilliSatoshis
                            FeeBaseMSat = feeBaseMSat |> LNMoney.MilliSatoshis
                            FeeProportionalMillionths = feeProportionalMillionths 
                            HTLCMaximumMSat = htlcMaximumMSat |> LNMoney.MilliSatoshis |> Some
                        }

                    let newUpdates =
                        let oldValue =
                            match updates |> Map.tryFind structuredShortChannelId with
                            | Some(updates) -> updates
                            | None -> ChannelUpdates.Empty
                        updates |> Map.add structuredShortChannelId (oldValue.With channelUpdate)

                    readUpdates (remainingCount - 1u) shortChannelId newUpdates

            let updates = readUpdates updatesCount 0UL Map.empty

            let gossipData = { Announcements = announcements; Updates = updates }

            // serialization roundtrip test
            use ms = new System.IO.MemoryStream()
            use rs = new System.IO.MemoryStream(ms.ToArray())
            let deserializedData = GossipData.Deserialize rs
            assert(gossipData = deserializedData)

            return ()
        }

