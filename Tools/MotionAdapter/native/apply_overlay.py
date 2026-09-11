"""Create the opt-in server source, without editing the pinned original."""
from pathlib import Path
import json
import shutil

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
LOCK = json.loads((ROOT / "upstream-lock.json").read_text(encoding="utf-8-sig"))
SOURCE = ROOT / "runtime" / ("llama.cpp-" + LOCK["llama_cpp"])
DEST = ROOT / "runtime" / "server-feature-src"


def replace(path, old, new):
    text = path.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise RuntimeError(f"Pinned source mismatch: {path.name}: {old[:100]!r}")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


def main():
    # copy overwrites only this specific build source; never removes an input tree.
    shutil.copytree(SOURCE, DEST, dirs_exist_ok=True, ignore=shutil.ignore_patterns(".git"))
    server = DEST / "tools" / "server"
    replace(server / "server-task.h", "    SERVER_TASK_TYPE_GET_LORA,", "    SERVER_TASK_TYPE_MOTION_FEATURE,\n    SERVER_TASK_TYPE_GET_LORA,")
    replace(server / "server-task.h", "    server_task_type type;", "    std::string motion_text;\n    std::string motion_request_id;\n    int64_t motion_submitted_ms = 0;\n    server_task_type type;")
    replace(server / "server-task.h", "struct completion_token_output {", "struct server_task_result_motion_feature : server_task_result {\n    json data;\n    json to_json() override { return data; }\n};\n\nstruct completion_token_output {")
    replace(server / "server-context.h", "    server_http_context::handler_t post_embeddings;", "    server_http_context::handler_t post_motion_features;\n    server_http_context::handler_t post_embeddings;")
    context = server / "server-context.cpp"
    replace(context, "    common_init_result_ptr llama_init;\n\n    llama_context * ctx = nullptr;", "    common_init_result_ptr llama_init;\n\n    llama_context * ctx = nullptr;\n    llama_context * motion_ctx = nullptr;\n    std::unique_ptr<server_task> motion_pending;\n#include \"feature-context.inc\"")
    replace(context, "    void destroy() {\n        llama_init.reset();", "    void destroy() {\n        motion_pending.reset();\n        if (motion_ctx) { llama_free(motion_ctx); motion_ctx = nullptr; }\n        llama_init.reset();")
    replace(context, "        vocab = llama_model_get_vocab(model);", "        vocab = llama_model_get_vocab(model);\n        if (!init_motion_context()) { return false; }")
    replace(context, "        switch (task.type) {\n            case SERVER_TASK_TYPE_COMPLETION:", "        switch (task.type) {\n            case SERVER_TASK_TYPE_MOTION_FEATURE: {\n                if (!motion_ctx) {\n                    send_error(task, \"Motion features unavailable: expected a 2048-dimensional Qwen model\", ERROR_TYPE_NOT_SUPPORTED);\n                } else if (motion_pending) {\n                    send_error(task, \"Motion feature queue is full; retry after current request\", ERROR_TYPE_UNAVAILABLE);\n                } else {\n                    motion_pending = std::make_unique<server_task>(std::move(task));\n                }\n            } break;\n            case SERVER_TASK_TYPE_COMPLETION:")
    replace(context, "                    // release slot linked with the task id", "                    if (motion_pending && motion_pending->id == task.id_target) { motion_pending.reset(); }\n                    // release slot linked with the task id")
    replace(context, "    void update_slots() {", "    void update_slots() {\n        expire_motion_request();")
    replace(context, "            if (all_idle) {\n                SRV_INF", "            if (all_idle) {\n                if (queue_tasks.queue_tasks_deferred_size() == 0) { process_motion_request(); }\n                SRV_INF")
    replace(context, "    this->get_health = [this]", "#include \"feature-route.inc\"\n\n    this->get_health = [this]")
    replace(server / "server.cpp", "    ctx_http.post(\"/embedding\",", "    ctx_http.post(\"/neeeva/motion-features\", ex_wrapper(routes.post_motion_features));\n    ctx_http.post(\"/embedding\",")
    for filename in ("feature-context.inc", "feature-route.inc"):
        shutil.copy2(HERE / filename, server / filename)
    print(DEST)


if __name__ == "__main__":
    main()
