namespace GWallet.Backend.UtxoCoin.Lightning

open System.IO
open System.Net.Http
open System.Linq

open Newtonsoft.Json
open NBitcoin
open DotNetLightning.Serialization
open DotNetLightning.Utils
open ResultUtils.Portability

module RapidGossipSyncer =
    
    let private RGSPrefix = [| 76uy; 68uy; 75uy; 1uy |]

    type CompactAnnouncment =
        {
            ChannelFeatures: Result<FeatureBits, FeatureError>
            ShortChannelId: ShortChannelId
            NodeId1: NodeId
            NodeId2: NodeId
        }

    type CompactChannelUpdate =
        {
            ShortChannelId: ShortChannelId
            CLTVExpiryDelta: uint16
            HtlcMinimumMSat: uint64
            FeeBaseMSat: uint32
            FeeProportionalMillionths: uint32
            HtlcMaximumMSat: uint64
        }

    let Sync () =
        async {
            use httpClient = new HttpClient()
            let! gossipData =
                httpClient.GetByteArrayAsync("https://rapidsync.lightningdevkit.org/snapshot/0")
                |> Async.AwaitTask

            use memStream = new MemoryStream(gossipData)
            use lightningReader = new LightningReaderStream(memStream)

            let prefix = Array.zeroCreate RGSPrefix.Length
            
            do! lightningReader.ReadAsync(prefix, 0, RGSPrefix.Length)
                |> Async.AwaitTask
                |> Async.Ignore

            if not (Enumerable.SequenceEqual (prefix, RGSPrefix)) then
                failwith "Invalid version prefix"

            let chainHash = lightningReader.ReadUInt256 true
            if chainHash <> Network.Main.GenesisHash then
                failwith "Invalid chain hash"

            let _lastSeenTimestamp = lightningReader.ReadUInt32 false

            let rec readNodeIds (remainingCount: uint) (state: list<NodeId>) =
                if remainingCount = 0u then
                    state
                else
                    let nodeId = lightningReader.ReadPubKey() |> NodeId
                    readNodeIds (remainingCount-1u) (state @ [ nodeId ])

            let nodeIds = readNodeIds (lightningReader.ReadUInt32 false) List.empty

            let rec readAnnouncements (remainingCount: uint) (previousShortChannelId: uint64) (state: list<CompactAnnouncment>) =
                if remainingCount = 0u then
                    state
                else
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

                    readAnnouncements (remainingCount-1u) shortChannelId (state @ [ compactAnn ])

            let announcements = readAnnouncements (lightningReader.ReadUInt32 false) 0UL List.empty

            let updatesCount = lightningReader.ReadUInt32 false

            let defaultCLTVExpiryDelta: uint16 = lightningReader.ReadUInt16 false
            let defaultHtlcMinimumMSat: uint64 = lightningReader.ReadUInt64 false
            let defaultFeeBaseMSat: uint32 = lightningReader.ReadUInt32 false
            let defaultFeeProportionalMillionths: uint32 = lightningReader.ReadUInt32 false
            let defaultHtlcMaximumMSat: uint64 = lightningReader.ReadUInt64 false

            let rec readUpdates (remainingCount: uint) (previousShortChannelId: uint64) (state: list<CompactChannelUpdate>) =
                if remainingCount = 0u then
                    state
                else
                    let shortChannelId = previousShortChannelId + lightningReader.ReadBigSize ()
                    let customChannelFlag = lightningReader.ReadByte()
                    
                    if customChannelFlag &&& 0b1000_0000uy > 0uy then
                        failwith "We don't support increamental updates yet!"        

                    let cltvExpiryDelta =
                        if customChannelFlag &&& 0b0100_0000uy > 0uy then
                            lightningReader.ReadUInt16 false
                        else
                            defaultCLTVExpiryDelta

                    let htlcMinimumMSat =
                        if customChannelFlag &&& 0b0010_0000uy > 0uy then
                            lightningReader.ReadUInt64 false
                        else
                            defaultHtlcMinimumMSat

                    let feeBaseMSat =
                        if customChannelFlag &&& 0b0001_0000uy > 0uy then
                            lightningReader.ReadUInt32 false
                        else
                            defaultFeeBaseMSat

                    let feeProportionalMillionths =
                        if customChannelFlag &&& 0b0000_1000uy > 0uy then
                            lightningReader.ReadUInt32 false
                        else
                            defaultFeeProportionalMillionths
                    
                    let htlcMaximumMSat =
                        if customChannelFlag &&& 0b0000_0100uy > 0uy then
                            lightningReader.ReadUInt64 false
                        else
                            defaultHtlcMaximumMSat

                    let compactUpdate =
                        {
                            ShortChannelId = shortChannelId |> ShortChannelId.FromUInt64
                            CLTVExpiryDelta = cltvExpiryDelta
                            HtlcMinimumMSat = htlcMinimumMSat
                            FeeBaseMSat = feeBaseMSat
                            FeeProportionalMillionths = feeProportionalMillionths
                            HtlcMaximumMSat = htlcMaximumMSat
                        }

                    readUpdates (remainingCount-1u) shortChannelId (state @ [ compactUpdate ])

            let updates =
                readUpdates updatesCount 0UL List.empty

            File.WriteAllText ("announcements.json",
                JsonConvert.SerializeObject(announcements, JsonMarshalling.SerializerSettings)
            )
            File.WriteAllText ("nodeIds.json",
                JsonConvert.SerializeObject(nodeIds, JsonMarshalling.SerializerSettings)
            )
            File.WriteAllText ("updates.json",
                JsonConvert.SerializeObject(updates, JsonMarshalling.SerializerSettings)
            )

            return ()
        }

