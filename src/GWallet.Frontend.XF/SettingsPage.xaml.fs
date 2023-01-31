namespace GWallet.Frontend.XF

open System
open System.Linq

open Xamarin.Forms
open Xamarin.Forms.Xaml
open Fsdk

open GWallet.Backend

type SettingsPage(option:string) as this =
    inherit ContentPage()
    let _ = base.LoadFromXaml(typeof<SettingsPage>)
    let titleLabel = base.FindByName<Label> "titleLabel"

    //check password
    let passwordEntry = base.FindByName<Entry> "passwordEntry"

    //check passphrase
    let phraseEntry = base.FindByName<Entry> "phraseEntry"
    let emailEntry = base.FindByName<Entry> "emailEntry"
    let dateOfBirthPicker = base.FindByName<DatePicker> "dateOfBirthPicker"
    let dateOfBirthLabel = base.FindByName<Entry> "dateOfBirthLabel"

    //loading & result
    let resultMessage = base.FindByName<Label> "resultMessage"
    let loadingIndicator= base.FindByName<ActivityIndicator> "loadingIndicator"
    let option = option;

    do
        this.Init()
                    
    member this.Init () =
        titleLabel.Text<-option;

    member this.OnCheckPasswordButtonClicked(_sender: Object, _args: EventArgs) =
        this.PerformCheck (Account.CheckValidPassword passwordEntry.Text None)

    member this.OnCheckSeedPassphraseClicked(_sender: Object, _args: EventArgs) =
        this.PerformCheck (Account.CheckValidSeed phraseEntry.Text dateOfBirthPicker.Date emailEntry.Text)
        
    member private this.PerformCheck(checkTask: Async<bool>) =
        async {
            loadingIndicator.IsVisible <- true
            resultMessage.IsVisible <- false
            let! checkResult = checkTask
            loadingIndicator.IsVisible <- false
            resultMessage.IsVisible <- true
            resultMessage.Text <- if checkResult then "Success!" else "Try again"
        } |> Async.StartImmediate

    member this.OnWipeWalletButtonClicked (_sender: Object, _args: EventArgs) =
        async {
           let! result = Application.Current.MainPage.DisplayAlert("Are you sure?", "Are you ABSOLUTELY SURE about this?", "Yes", "No") |> Async.AwaitTask
           if result then
                Account.WipeAll()
                let displayTask = Application.Current.MainPage.DisplayAlert("Success", "You successfully wiped your current wallet", "Ok")
                do! Async.AwaitTask displayTask

        } |> Async.StartImmediate

    member this.datePicker_DateSelected (_: obj) (e: Xamarin.Forms.DateChangedEventArgs) =
        dateOfBirthLabel.Text <- e.NewDate.ToString("dd MMMM yyyy")




