using Content.Client.Message;
using Content.Shared._Stalker_OW.PublicName;
using Content.Shared.CCVar;
using Robust.Shared.Utility;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private void InitializeNicknameControls()
    {
        NicknameEdit.IsValid = text => text.Length <= STNicknameRules.MaxLength;
        NicknameEdit.OnTextChanged += args => SetNickname(args.Text);
        NicknameDogtagCheckBox.OnToggled += args => SetUseNicknameOnDogtag(args.Pressed);

        NameSettingsButton.OnToggled += args => NameSettingsContainer.Visible = args.Pressed;
        NicknameDisplayButton.AddItem(Loc.GetString("st-profile-editor-display-nickname"),
            (int) STNicknameDisplay.NicknameOnly);
        NicknameDisplayButton.AddItem(Loc.GetString("st-profile-editor-display-full"),
            (int) STNicknameDisplay.FullName);
        NicknameDisplayButton.OnItemSelected += args =>
        {
            if (Profile == null)
                return;

            NicknameDisplayButton.SelectId(args.Id);
            Profile = Profile.WithSTNicknameDisplayMode((STNicknameDisplay) args.Id);
            SetDirty();
            UpdateNicknamePreview();
        };
    }

    private void SetNickname(string nickname)
    {
        if (Profile == null)
            return;

        Profile = Profile.WithSTNickname(nickname);
        SetDirty();
        UpdateNicknamePreview();
    }

    private void SetUseNicknameOnDogtag(bool useNickname)
    {
        if (Profile == null)
            return;

        Profile = Profile.WithSTUseNicknameOnDogtag(useNickname);
        SetDirty();
        UpdateNicknamePreview();
    }

    private void UpdateNicknameControls()
    {
        NicknameEdit.Text = Profile?.STNickname ?? string.Empty;
        NicknameDogtagCheckBox.Pressed = Profile?.STUseNicknameOnDogtag ?? false;
        NicknameDisplayButton.SelectId((int) STNicknameRules.ValidateDisplay(
            Profile?.STNicknameDisplayMode ?? STNicknameDisplay.NicknameOnly));
        UpdateNicknamePreview();
    }
    
    private void UpdateNicknamePreview()
    {
        if (Profile == null)
        {
            NicknamePreviewLabel.SetMessage(string.Empty);
            return;
        }

        var realName = Profile.Name;
        var nickname = STNicknameRules.Sanitize(Profile.STNickname, _cfgManager.GetCVar(CCVars.RestrictedNames));
        if (nickname == realName)
            nickname = string.Empty;

        var hasNickname = !string.IsNullOrEmpty(nickname);
        var publicName = STNicknameRules.FormatDisplayName(realName, nickname, Profile.STNicknameDisplayMode);

        NicknameDogtagCheckBox.Disabled = !hasNickname;
        NicknameDisplayButton.Disabled = !hasNickname;

        NicknamePreviewLabel.SetMarkup(Loc.GetString("st-profile-editor-nickname-preview",
            ("publicName", FormattedMessage.EscapeText(publicName))));
    }
}
