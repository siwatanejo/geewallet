﻿namespace GWallet.Backend.UtxoCoin.Lightning

open GWallet.Backend

open NBitcoin

open GWallet.Backend.UtxoCoin

module ScriptManager =

    // Used for e.g. option_upfront_shutdown_script in
    // https://github.com/lightningnetwork/lightning-rfc/blob/master/02-peer-protocol.md#rationale-4
    let CreatePayoutScript (account: IUtxoAccount) =
        let baseAccount = account :> IAccount
        let network = Account.GetNetwork baseAccount.Currency
        let scriptAddress = BitcoinScriptAddress (baseAccount.PublicAddress, network)
        scriptAddress.ScriptPubKey
