using FluentValidation;
using SimPle.Application.Friends.DTOs;

namespace SimPle.Application.Friends.Validators;

public sealed class UpdateFriendSettingsValidator : AbstractValidator<UpdateFriendSettingsRequestDto>
{
    private static readonly HashSet<string> ValidPrivacyValues =
        new(StringComparer.OrdinalIgnoreCase) { "Anyone", "FriendsOfFriends", "Off" };

    private static readonly HashSet<string> ValidSearchVisibilityValues =
        new(StringComparer.OrdinalIgnoreCase) { "Everyone", "FriendsOfFriends", "Nobody" };

    private static readonly HashSet<string> ValidFriendsListVisibilityValues =
        new(StringComparer.OrdinalIgnoreCase) { "Everyone", "Friends", "OnlyMe" };

    public UpdateFriendSettingsValidator()
    {
        RuleFor(x => x.FriendRequestPrivacy)
            .NotEmpty()
            .Must(v => ValidPrivacyValues.Contains(v))
            .WithMessage("FriendRequestPrivacy must be one of: Anyone, FriendsOfFriends, Off.");

        RuleFor(x => x.SearchVisibility)
            .Must(v => v is null || ValidSearchVisibilityValues.Contains(v))
            .WithMessage("SearchVisibility must be one of: Everyone, FriendsOfFriends, Nobody.");

        RuleFor(x => x.FriendsListVisibility)
            .Must(v => v is null || ValidFriendsListVisibilityValues.Contains(v))
            .WithMessage("FriendsListVisibility must be one of: Everyone, Friends, OnlyMe.");
    }
}
