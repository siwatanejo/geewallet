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

    let Sync () =
        async {
            use httpClient = new HttpClient()
            // Always do a full sync
            let! gossipData =
                httpClient.GetByteArrayAsync("https://rapidsync.lightningdevkit.org/snapshot/0")
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

            let rec readNodeIds (remainingCount: uint) (state: list<NodeId>) =
                if remainingCount = 0u then
                    state
                else
                    let nodeId = lightningReader.ReadPubKey() |> NodeId
                    readNodeIds (remainingCount-1u) (state @ [ nodeId ])

            let nodeIds = readNodeIds (lightningReader.ReadUInt32 false) List.empty

            let announcementsCount = lightningReader.ReadUInt32 false
            let announcements = MutableList<CompactAnnouncment>(int announcementsCount)

            let rec readAnnouncements (remainingCount: uint) (previousShortChannelId: uint64) =
                if remainingCount = 0u then
                    ()
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

                    announcements.Add compactAnn
                    readAnnouncements (remainingCount-1u) shortChannelId

            readAnnouncements announcementsCount 0UL

            let updatesCount = lightningReader.ReadUInt32 false

            let defaultCLTVExpiryDelta: uint16 = lightningReader.ReadUInt16 false
            let defaultHtlcMinimumMSat: uint64 = lightningReader.ReadUInt64 false
            let defaultFeeBaseMSat: uint32 = lightningReader.ReadUInt32 false
            let defaultFeeProportionalMillionths: uint32 = lightningReader.ReadUInt32 false
            let defaultHtlcMaximumMSat: uint64 = lightningReader.ReadUInt64 false

            let updates = MutableList(int updatesCount)

            let rec readUpdates (remainingCount: uint) (previousShortChannelId: uint64) =
                if remainingCount = 0u then
                    ()
                else
                    let shortChannelId = previousShortChannelId + lightningReader.ReadBigSize ()
                    let customChannelFlag = lightningReader.ReadByte()
                    let standardChannelFlag = customChannelFlag &&& 0b0000_0011uy

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

                    let channelUpdate =
                        {
                            UnsignedChannelUpdateMsg.ShortChannelId = shortChannelId |> ShortChannelId.FromUInt64
                            Timestamp = backdatedTimestamp
                            ChainHash = Network.Main.GenesisHash
                            ChannelFlags = standardChannelFlag
                            MessageFlags = 0uy
                            CLTVExpiryDelta = cltvExpiryDelta |> BlockHeightOffset16
                            HTLCMinimumMSat = htlcMinimumMSat |> LNMoney.MilliSatoshis
                            FeeBaseMSat = feeBaseMSat |> LNMoney.MilliSatoshis
                            FeeProportionalMillionths = feeProportionalMillionths 
                            HTLCMaximumMSat = htlcMaximumMSat |> LNMoney.MilliSatoshis |> Some
                        }

                    updates.Add channelUpdate
                    readUpdates (remainingCount-1u) shortChannelId

            readUpdates updatesCount 0UL

            return ()
        }

