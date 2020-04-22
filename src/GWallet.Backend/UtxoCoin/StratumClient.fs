﻿namespace GWallet.Backend.UtxoCoin

open System

open Newtonsoft.Json

open GWallet.Backend
open GWallet.Backend.FSharpUtil.UwpHacks

// can't make this type below private, or else Newtonsoft.Json will serialize it incorrectly
type Request =
    {
        Id: int;
        Method: string;
        Params: obj
    }

type ServerVersionResult =
    {
        Id: int;
        Result: array<string>;
    }

type BlockchainScriptHashGetBalanceInnerResult =
    {
        Confirmed: Int64;
        Unconfirmed: Int64;
    }
type BlockchainScriptHashGetBalanceResult =
    {
        Id: int;
        Result: BlockchainScriptHashGetBalanceInnerResult
    }

type BlockchainScriptHashListUnspentInnerResult =
    {
        TxHash: string;
        TxPos: int;
        Value: Int64;
        Height: Int64;
    }
type BlockchainScriptHashListUnspentResult =
    {
        Id: int;
        Result: array<BlockchainScriptHashListUnspentInnerResult>
    }

type BlockchainScriptHashHistoryInnerResult =
    {
        TxHash: string
        Height: uint32
    }

type BlockchainScriptHashMerkleInnerResult =
    {
        BlockHeight: uint32
        Merkle: List<string>
        Pos: uint32
    }

type BlockchainScriptHashHistoryResult =
    {
        Id: int
        Result: List<BlockchainScriptHashHistoryInnerResult>
    }

type BlockchainScriptHashMerkleResult =
    {
        Id: int
        Result: BlockchainScriptHashMerkleInnerResult
    }

type BlockchainTransactionGetResult =
    {
        Id: int;
        Result: string;
    }

type VerboseResult =
    {
        Locktime: uint32
        Confirmations: uint32
    }

type BlockchainTransactionGetVerboseResult =
    {
        Id: int
        Result: VerboseResult
    }

type BlockchainEstimateFeeResult =
    {
        Id: int;
        Result: decimal;
    }

type BlockchainTransactionBroadcastResult =
    {
        Id: int;
        Result: string;
    }

type BlockchainHeadersSubscribeInnerResult =
    {
        Height: int
        Hex: string
    }

type BlockchainHeadersSubscribeResult =
    {
        Id: int
        Result: BlockchainHeadersSubscribeInnerResult
    }

type ErrorInnerResult =
    {
        Message: string;
        Code: int;
    }

type ErrorResult =
    {
        Id: int;
        Error: ErrorInnerResult;
    }

type RpcErrorCode =
    // see https://gitlab.gnome.org/World/geewallet/issues/110
    | ExcessiveResourceUsage = -101

    // see https://gitlab.gnome.org/World/geewallet/issues/117
    | ServerBusy = -102

    // see git commit msg of 0aba03a8291daa526fde888d0c02a789abe411f2
    | InternalError = -32603

    // see https://gitlab.gnome.org/World/geewallet/issues/112
    | UnknownMethod = -32601

type public ElectrumServerReturningErrorInJsonResponseException(message: string, code: int) =
    inherit CommunicationUnsuccessfulException(message)

    member val ErrorCode: int =
        code with get

    member this.InternalErrors(): seq<ElectrumServerReturningErrorInJsonResponseException> =
        let rec searchForOpenBracketFrom(openSearchIndex: int) = seq {
            if openSearchIndex < message.Length then
                let openIndex = message.IndexOf('{', openSearchIndex)
                if openIndex <> -1 then
                    let rec searchForCloseBracketFrom(closeSearchIndex: int) = seq {
                        if closeSearchIndex < message.Length then
                            let closeIndex = message.IndexOf('}', closeSearchIndex)
                            if closeIndex <> -1 then
                                yield message.[openIndex..closeIndex]
                                yield! searchForCloseBracketFrom(closeIndex + 1)
                    }
                    yield! searchForCloseBracketFrom(openIndex + 1)
                    yield! searchForOpenBracketFrom(openIndex + 1)
        }
        searchForOpenBracketFrom 0
        |> Seq.choose (fun (possibleJsonString: string) ->
            if possibleJsonString.Contains("\"") then
                None
            else
                let possibleJsonString = possibleJsonString.Replace('\'', '"')
                try
                    let maybeError =
                        JsonConvert.DeserializeObject<ErrorInnerResult>(
                            possibleJsonString,
                            Marshalling.PascalCase2LowercasePlusUnderscoreConversionSettings
                        )
                    if (not (Object.ReferenceEquals(maybeError, null))) then
                        Some <| ElectrumServerReturningErrorInJsonResponseException(maybeError.Message, maybeError.Code)
                    else
                        None
                with
                | ex -> None
        )

type public ElectrumServerReturningErrorException(message: string, code: int,
                                                  originalRequest: string, originalResponse: string) =
    inherit ElectrumServerReturningErrorInJsonResponseException(message, code)

    member val OriginalRequest: string =
        originalRequest with get

    member val OriginalResponse: string =
        originalResponse with get

type public ElectrumServerReturningInternalErrorException(message: string, code: int,
                                                          originalRequest: string, originalResponse: string) =
    inherit ElectrumServerReturningErrorException(message, code, originalRequest, originalResponse)

type StratumClient (jsonRpcClient: JsonRpcTcpClient) =

    let Serialize(req: Request): string =
        JsonConvert.SerializeObject(req, Formatting.None,
                                    Marshalling.PascalCase2LowercasePlusUnderscoreConversionSettings)

    // TODO: add 'T as incoming request type, leave 'R as outgoing response type
    member private self.Request<'R> (jsonRequest: string): Async<'R*string> = async {
        let! rawResponse = jsonRpcClient.Request jsonRequest

        // FIXME: we should actually fix this bug in JsonRpcSharp (https://github.com/nblockchain/JsonRpcSharp/issues/9)
        if String.IsNullOrEmpty rawResponse then
            return raise <| ProtocolGlitchException(SPrintF2 "Server '%s' returned a null/empty JSON response to the request '%s'??"
                                                             jsonRpcClient.Host jsonRequest)

        try
            return (StratumClient.Deserialize<'R> rawResponse, rawResponse)
        with
        | :? ElectrumServerReturningErrorInJsonResponseException as ex ->
            if ex.ErrorCode = int RpcErrorCode.InternalError then
                return raise(ElectrumServerReturningInternalErrorException(ex.Message, ex.ErrorCode, jsonRequest, rawResponse))
            if ex.ErrorCode = int RpcErrorCode.UnknownMethod then
                return raise <| ServerMisconfiguredException(ex.Message, ex)
            if ex.ErrorCode = int RpcErrorCode.ServerBusy then
                return raise <| ServerUnavailabilityException(ex.Message, ex)
            if ex.ErrorCode = int RpcErrorCode.ExcessiveResourceUsage then
                return raise <| ServerUnavailabilityException(ex.Message, ex)

            return raise(ElectrumServerReturningErrorException(ex.Message, ex.ErrorCode, jsonRequest, rawResponse))
    }

    static member public Deserialize<'T> (result: string): 'T =
        let resultTrimmed = result.Trim()
        let maybeError =
            try
                JsonConvert.DeserializeObject<ErrorResult>(resultTrimmed,
                                                           Marshalling.PascalCase2LowercasePlusUnderscoreConversionSettings)
            with
            | ex -> raise <| Exception(SPrintF2 "Failed deserializing JSON response (to check for error) '%s' to type '%s'"
                                               resultTrimmed typedefof<'T>.FullName, ex)

        if (not (Object.ReferenceEquals(maybeError, null))) && (not (Object.ReferenceEquals(maybeError.Error, null))) then
            raise(ElectrumServerReturningErrorInJsonResponseException(maybeError.Error.Message, maybeError.Error.Code))

        let deserializedValue =
            try
                JsonConvert.DeserializeObject<'T>(resultTrimmed,
                                                  Marshalling.PascalCase2LowercasePlusUnderscoreConversionSettings)
            with
            | ex -> raise <| Exception(SPrintF2 "Failed deserializing JSON response '%s' to type '%s'"
                                                resultTrimmed typedefof<'T>.FullName, ex)

        if Object.ReferenceEquals(deserializedValue, null) then
            failwith <| SPrintF2 "Failed deserializing JSON response '%s' to type '%s' (result was null)"
                      resultTrimmed typedefof<'T>.FullName

        deserializedValue

    member self.BlockchainScriptHashGetBalance address: Async<BlockchainScriptHashGetBalanceResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.scripthash.get_balance";
            Params = [address]
        }
        let json = Serialize obj

        async {
            let! resObj,_ = self.Request<BlockchainScriptHashGetBalanceResult> json
            return resObj
        }

    static member private CreateVersion(versionStr: string): Version =
        let correctedVersion =
            if (versionStr.EndsWith("+")) then
                versionStr.Substring(0, versionStr.Length - 1)
            else
                versionStr
        try
            Version(correctedVersion)
        with
        | exn -> raise(Exception("Electrum Server's version disliked by .NET Version class: " + versionStr, exn))

    member self.ServerVersion (clientName: string) (protocolVersion: Version): Async<Version> = async {
        let obj = {
            Id = 0;
            Method = "server.version";
            Params = [clientName; protocolVersion.ToString()]
        }
        // this below serializes to:
        //  (SPrintF2 "{ \"id\": 0, \"method\": \"server.version\", \"params\": [ \"%s\", \"%s\" ] }"
        //      CURRENT_ELECTRUM_FAKED_VERSION PROTOCOL_VERSION)
        let json = Serialize obj
        let! resObj, rawResponse = self.Request<ServerVersionResult> json

        if Object.ReferenceEquals (resObj, null) then
            failwith <| SPrintF1 "resObj is null?? raw response was %s" rawResponse

        if Object.ReferenceEquals (resObj.Result, null) then
            failwith <| SPrintF1 "resObj.Result is null?? raw response was %s" rawResponse

        // resObj.Result.[0] is e.g. "ElectrumX 1.4.3"
        // e.g. "1.1"
        let serverProtocolVersion = resObj.Result.[1]

        return StratumClient.CreateVersion(serverProtocolVersion)
    }

    member self.BlockchainScriptHashListUnspent address: Async<BlockchainScriptHashListUnspentResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.scripthash.listunspent";
            Params = [address]
        }
        let json = Serialize obj
        async {
            let! resObj,_ = self.Request<BlockchainScriptHashListUnspentResult> json
            return resObj
        }

    member self.BlockchainScriptHashHistory scriptHash: Async<BlockchainScriptHashHistoryResult> =
        let obj = {
            Id = 0
            Method = "blockchain.scripthash.get_history"
            Params = [scriptHash]
        }
        let json = Serialize obj
        async {
            let! resObj,_ = self.Request<BlockchainScriptHashHistoryResult> json
            return resObj
        }

    member self.BlockchainScriptHashMerkle txHash height: Async<BlockchainScriptHashMerkleResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.transaction.get_merkle";
            Params = Map.ofList ["tx_hash", txHash :> obj; "height", height :> obj]
        }
        let json = Serialize obj
        async {
            let! resObj,_ = self.Request<BlockchainScriptHashMerkleResult> json
            return resObj
        }

    member self.BlockchainTransactionGet txHash: Async<BlockchainTransactionGetResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.transaction.get";
            Params = [txHash]
        }
        let json = Serialize obj
        async {
            let! resObj,_ = self.Request<BlockchainTransactionGetResult> json
            return resObj
        }

    member self.BlockchainTransactionGetVerbose (txHash: string): Async<BlockchainTransactionGetVerboseResult> =
        let obj = {
            Id = 0
            Method = "blockchain.transaction.get"
            Params = Map.ofList ["tx_hash", txHash :> obj; "verbose", true :> obj]
        }
        let json = Serialize obj
        async {
            let! resObj,_ = self.Request<BlockchainTransactionGetVerboseResult> json
            return resObj
        }

    member self.BlockchainEstimateFee (numBlocksTarget: int): Async<BlockchainEstimateFeeResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.estimatefee";
            Params = [numBlocksTarget]
        }
        let json = Serialize obj

        async {
            let! resObj,_ = self.Request<BlockchainEstimateFeeResult> json
            return resObj
        }

    member self.BlockchainTransactionBroadcast txInHex: Async<BlockchainTransactionBroadcastResult> =
        let obj = {
            Id = 0;
            Method = "blockchain.transaction.broadcast";
            Params = [txInHex]
        }
        let json = Serialize obj

        async {
            let! resObj,_ = self.Request<BlockchainTransactionBroadcastResult> json
            return resObj
        }

    member self.BlockchainHeadersSubscribe (): Async<BlockchainHeadersSubscribeResult> =
        let obj = {
            Id = 0
            Method = "blockchain.headers.subscribe"
            Params = []
        }
        let json = Serialize obj

        async {
            let! resObj,_ = self.Request<BlockchainHeadersSubscribeResult> json
            return resObj
        }
