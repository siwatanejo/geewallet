namespace GWallet.Backend.LtcMWCoin

open NLitecoin.MimbleWimble

open GWallet.Backend

type internal ILtcMWAccount =
    inherit IAccount

    abstract Keychain: Wallet.KeyChain
