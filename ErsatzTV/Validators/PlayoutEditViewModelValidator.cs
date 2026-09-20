using ErsatzTV.ViewModels;
using FluentValidation;
using FluentValidation.Results;

namespace ErsatzTV.Validators;

public class PlayoutEditViewModelValidator : AbstractValidator<PlayoutEditViewModel>
{
    public PlayoutEditViewModelValidator()
    {
        RuleFor(p => p.Channel).NotNull();
        RuleFor(p => p.ProgramSchedule).NotNull().When(p => string.IsNullOrWhiteSpace(p.Kind));
        RuleFor(p => p.ScheduleFile).NotEmpty().When(p =>
            p.Kind is PlayoutKind.ExternalJson or PlayoutKind.Sequential or PlayoutKind.Scripted);
    }

    public Func<object, string, Task<IEnumerable<string>>> ValidateValue => async (model, propertyName) =>
    {
        ValidationResult result = await ValidateAsync(
            ValidationContext<PlayoutEditViewModel>.CreateWithOptions(
                (PlayoutEditViewModel)model,
                x => x.IncludeProperties(propertyName)));
        if (result.IsValid)
        {
            return [];
        }

        return result.Errors.Select(e => e.ErrorMessage);
    };
}
