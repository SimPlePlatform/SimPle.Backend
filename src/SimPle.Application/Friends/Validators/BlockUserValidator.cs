using FluentValidation;
using SimPle.Application.Friends.DTOs;

namespace SimPle.Application.Friends.Validators;

public sealed class BlockUserValidator : AbstractValidator<BlockUserRequestDto>
{
    public BlockUserValidator()
    {
        RuleFor(x => x.TargetUserId).NotEmpty();
    }
}
