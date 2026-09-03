#!/usr/bin/python3

import argparse
import importlib
import os
import sys

from uuid import UUID

import etv_client
from etv_client.api import ScriptedScheduleApi

def main():
    parser = argparse.ArgumentParser(description="Run an ETV scripted schedule")
    parser.add_argument('host', help="The ETV host (e.g., http://localhost:8409)")
    parser.add_argument('build_id', type=UUID, help="The build ID for the playout")
    parser.add_argument('mode', choices=['reset', 'continue'], help="The playout build mode")
    parser.add_argument('script_name', help="The name of the script module to use (e.g., one)")

    known_args, unknown_args = parser.parse_known_args()

    try:
        script_module = importlib.import_module(f"scripts.{known_args.script_name}")
    except ImportError:
        print(f"Error: cannot find a script file named '{known_args.script_name}.py' in the 'scripts' directory.")
        sys.exit(1)

    configuration = etv_client.Configuration(host=known_args.host)

    with etv_client.ApiClient(configuration) as api_client:
        api_key = os.environ.get("ETV_API_KEY")
        if not api_key:
            print("Error: ETV_API_KEY is not set; this script must be launched by ErsatzTV.")
            sys.exit(1)
        api_client.set_default_header("X-Etv-Api-Key", api_key)

        try:
            define_content = getattr(script_module, 'define_content')
            reset_playout = getattr(script_module, 'reset_playout')
            build_playout = getattr(script_module, 'build_playout')

            api_instance = ScriptedScheduleApi(api_client)

            context = api_instance.get_context(known_args.build_id)

            define_content(api_instance, context, known_args.build_id)

            if known_args.mode == "reset":
                new_context = reset_playout(api_instance, context, known_args.build_id)
                context = new_context or api_instance.get_context(known_args.build_id)

            build_playout(api_instance, context, known_args.build_id)
        except etv_client.ApiException as e:
            print(f"Exception when calling scripted schedule api: {e}\n")
        except AttributeError as e:
            print(f"Error: the '{known_args.script_name}' script is missing a required function. {e}")

if __name__ == "__main__":
    main()

