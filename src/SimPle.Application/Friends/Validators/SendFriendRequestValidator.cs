using FluentValidation;
using SimPle.Application.Friends.DTOs;

namespace SimPle.Application.Friends.Validators;

public sealed class SendFriendRequestValidator : AbstractValidator<SendFriendRequestDto>
{
    public SendFriendRequestValidator()
    {
        RuleFor(x => x.TargetUserId).NotEmpty();
    }
}
