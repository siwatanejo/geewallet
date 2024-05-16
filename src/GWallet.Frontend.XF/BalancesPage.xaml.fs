#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
// We have unused variables because they are in the #XAMARIN sections.
// this should be removed once we fully integrate this code.
#nowarn "1182"
#endif

open System
open System.Linq
open System.Threading
open System.Threading.Tasks

#if !XAMARIN
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Xaml
open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Networking
#else
open Xamarin.Forms
open Xamarin.Forms.Xaml
open Xamarin.Essentials
#endif

open Fsdk

#if XAMARIN
open GWallet.Frontend.XF.Controls
#else
open GWallet.Frontend.Maui.Controls
#endif
open GWallet.Backend
open GWallet.Backend.FSharpUtil.UwpHacks


type FiatAmountFrameTapHandler = unit -> unit

// this type allows us to represent the idea that if we have, for example, 3 LTC and an unknown number of ETC (might
// be because all ETC servers are unresponsive), then it means we have AT LEAST 3LTC; as opposed to when we know for
// sure all balances of all currencies because all servers are responsive
type TotalBalance =
    | ExactBalance of decimal
    | AtLeastBalance of decimal
    static member (+) (x: TotalBalance, y: decimal) =
        match x with
        | ExactBalance exactBalance -> ExactBalance (exactBalance + y)
        | AtLeastBalance exactBalance -> AtLeastBalance (exactBalance + y)
    static member (+) (x: decimal, y: TotalBalance) =
        y + x

type BalancesPage(state: FrontendHelpers.IGlobalAppState,
                  normalBalanceStates: seq<BalanceState>,
                  readOnlyBalanceStates: seq<BalanceState>,
                  currencyImages: Map<Currency*bool,Image>,
                  startWithReadOnlyAccounts: bool)
                      as self =
    inherit ContentPage()

    let _ = base.LoadFromXaml(typeof<BalancesPage>)

    let normalAccountsBalanceSets = normalBalanceStates.Select(fun balState -> balState.BalanceSet)
    let readOnlyAccountsBalanceSets = readOnlyBalanceStates.Select(fun balState -> balState.BalanceSet)
    let mainLayout = base.FindByName<Grid> "mainLayout"
    let totalFiatAmountLabel = mainLayout.FindByName<Label> "totalFiatAmountLabel"
    let totalReadOnlyFiatAmountLabel = mainLayout.FindByName<Label> "totalReadOnlyFiatAmountLabel"
    let contentLayout = base.FindByName<Grid> "contentLayout"
    let normalChartView = base.FindByName<CircleChartView> "normalChartView"
    let readonlyChartView = base.FindByName<CircleChartView> "readonlyChartView"

    let standardTimeToRefreshBalances = TimeSpan.FromMinutes 5.0
    let standardTimeToRefreshBalancesWhenThereIsImminentIncomingPaymentOrNotEnoughInfoToKnow = TimeSpan.FromMinutes 1.0
    let timerStartDelay = TimeSpan.FromMilliseconds 500.
    
    let rec FindCryptoBalances (cryptoBalanceClassId: string) (layout: Grid) 
                               (elements: List<View>) (resultsSoFar: List<Frame>): List<Frame> =
        match elements with
        | [] -> resultsSoFar
        | head::tail ->
            match head with
            | :? Frame as frame ->
                let newResults =
                    if frame.ClassId = cryptoBalanceClassId then
                        frame::resultsSoFar
                    else
                        resultsSoFar
                FindCryptoBalances cryptoBalanceClassId layout tail newResults
            | _ ->
                FindCryptoBalances cryptoBalanceClassId layout tail resultsSoFar

    let GetAmountOrDefault maybeAmount =
        match maybeAmount with
        | NotFresh NotAvailable ->
            0m
        | Fresh amount | NotFresh (Cached (amount,_)) ->
            amount

    let RedrawCircleView (readOnly: bool) (balances: seq<BalanceState>) =
        let chartView =
            if readOnly then
                readonlyChartView
            else
                normalChartView
        let fullAmount = balances.Sum(fun b -> GetAmountOrDefault b.FiatAmount)

        let chartSourceList = 
            balances |> Seq.map (fun balanceState ->
                 let percentage = 
                     if fullAmount = 0m then
                         0m
                     else
                         GetAmountOrDefault balanceState.FiatAmount / fullAmount
                 { 
                     Color = FrontendHelpers.GetCryptoColor balanceState.BalanceSet.Account.Currency
                     Percentage = float(percentage)
                 }
            )
        chartView.SegmentsSource <- chartSourceList

    let GetBaseRefreshInterval() =
        if self.NoImminentIncomingPayment then
            standardTimeToRefreshBalances
        else
            standardTimeToRefreshBalancesWhenThereIsImminentIncomingPaymentOrNotEnoughInfoToKnow

    // default value of the below field is 'false', just in case there's an incoming payment which we don't want to miss
    let mutable noImminentIncomingPayment = false

    let lockObject = Object()

    do
        self.Init()

    [<Obsolete(DummyPageConstructorHelper.Warning)>]
    new() = BalancesPage(DummyPageConstructorHelper.GlobalFuncToRaiseExceptionIfUsedAtRuntime(),Seq.empty,Seq.empty,
                         Map.empty,false)

    member private __.NoImminentIncomingPayment
        with get() = lock lockObject (fun _ -> noImminentIncomingPayment)
         and set value = lock lockObject (fun _ -> noImminentIncomingPayment <- value)

    member self.PopulateBalances (readOnly: bool) (balances: seq<BalanceState>) =
        let activeCurrencyClassId,inactiveCurrencyClassId =
            FrontendHelpers.GetActiveAndInactiveCurrencyClassIds readOnly

        let contentLayoutChildrenList =
#if XAMARIN
            (contentLayout.Children |> List.ofSeq)
#else
            contentLayout.Children |> Seq.choose (function :? View as view -> Some view | _ -> None) |> List.ofSeq
#endif

        let activeCryptoBalances = FindCryptoBalances activeCurrencyClassId 
                                                      contentLayout 
                                                      contentLayoutChildrenList
                                                      List.Empty

        let inactiveCryptoBalances = FindCryptoBalances inactiveCurrencyClassId 
                                                        contentLayout 
                                                        contentLayoutChildrenList
                                                        List.Empty

        contentLayout.BatchBegin()                      

        for inactiveCryptoBalance in inactiveCryptoBalances do
            inactiveCryptoBalance.IsVisible <- false

        //We should create new frames only once, then just play with IsVisible(True|False) 
        if activeCryptoBalances.Any() then
            for activeCryptoBalance in activeCryptoBalances do
                activeCryptoBalance.IsVisible <- true
        else
            for _ in balances do
                contentLayout.RowDefinitions.Add(
                    RowDefinition(Height = GridLength.Auto)
                )
            
            balances |> Seq.iteri (fun balanceIndex balanceState ->
                let balanceSet = balanceState.BalanceSet
                let tapGestureRecognizer = TapGestureRecognizer()
               
                tapGestureRecognizer.Tapped.Subscribe(fun _ ->
                    let receivePage () =
                        ReceivePage()
                            :> Page
                    FrontendHelpers.SwitchToNewPage self receivePage true
                ) |> ignore
                
                let frame = balanceSet.Widgets.Frame
                frame.GestureRecognizers.Add tapGestureRecognizer
                contentLayout.Children.Add frame
#if XAMARIN
                Grid.SetRow(frame, balanceIndex)
#else
                contentLayout.SetRow(frame, balanceIndex)
#endif
            )
        contentLayout.BatchCommit()

    member self.PopulateGridInitially () =
        self.PopulateBalances false normalBalanceStates
        RedrawCircleView false normalBalanceStates



    member private self.Init () =
#if XAMARIN
        if Device.RuntimePlatform = Device.GTK then
            // workaround layout issues in Xamarin.Forms/GTK
            mainLayout.RowDefinitions.[1] <- RowDefinition(
                Height = GridLength 550.0
            )
#endif
        
        MainThread.BeginInvokeOnMainThread(fun _ ->
            self.PopulateGridInitially ()
        )
