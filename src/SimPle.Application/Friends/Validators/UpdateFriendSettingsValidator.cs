using FluentValidation;
using SimPle.Application.Friends.DTOs;

namespace SimPle.Application.Friends.Validators;

public sealed class UpdateFriendSettingsValidator : AbstractValidator<UpdateFriendSettingsRequestDto>
{
    private static readonly HashSet<string> ValidValues =
        new(StringComparer.OrdinalIgnoreCase) { "Anyone", "FriendsOfFriends", "Off" };

    public UpdateFriendSettingsValidator()
    {
        RuleFor(x => x.FriendRequestPrivacy)
            .NotEmpty()
            .Must(v => ValidValues.Contains(v))
            .WithMessage("FriendRequestPrivacy must be one of: Anyone, FriendsOfFriends, Off.");
    }
}
