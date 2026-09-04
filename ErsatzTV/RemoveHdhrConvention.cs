using System.Reflection;
using ErsatzTV.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace ErsatzTV;

public class RemoveHdhrConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        Option<ControllerModel> toRemove = None;
        foreach (ControllerModel controller in application.Controllers)
        {
            if (controller.ControllerType == typeof(HdhrController).GetTypeInfo())
            {
                toRemove = controller;
                break;
            }
        }

        foreach (ControllerModel remove in toRemove)
        {
            application.Controllers.Remove(remove);
        }
    }
}
