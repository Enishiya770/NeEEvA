// Windows C-API pilot: one llama_model, two isolated contexts, no second Qwen load.
// Links at runtime to the user's pinned llama.cpp DLLs. No CUDA toolkit is needed to build.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "llama.h"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include <chrono>

template<class T> T symbol(HMODULE module, const char * name) {
    auto ptr = GetProcAddress(module, name);
    if (!ptr) throw std::runtime_error(std::string("Missing DLL symbol: ") + name);
    return reinterpret_cast<T>(ptr);
}
#define LOAD(name) auto f_##name = symbol<decltype(&name)>(lib, #name)

int main(int argc, char **argv) {
    if (argc != 6) {
        std::cerr << "Usage: qwen_feature_probe DLL_DIRECTORY MODEL_GGUF PROMPTS_UTF8 FEATURES_F32 REPORT_JSON\n";
        return 2;
    }
    try {
        std::string dll_dir = argv[1];
        SetDllDirectoryA(dll_dir.c_str());
        HMODULE ggml = LoadLibraryA((dll_dir + "\\ggml.dll").c_str());
        if (!ggml) throw std::runtime_error("Cannot load ggml.dll");
        auto load_backends = symbol<decltype(&ggml_backend_load_all_from_path)>(ggml, "ggml_backend_load_all_from_path");
        load_backends(dll_dir.c_str());
        HMODULE lib = LoadLibraryA((dll_dir + "\\llama.dll").c_str());
        if (!lib) throw std::runtime_error("Cannot load llama.dll");
        LOAD(llama_backend_init); LOAD(llama_backend_free);
        LOAD(llama_model_default_params); LOAD(llama_context_default_params);
        LOAD(llama_model_load_from_file); LOAD(llama_model_free); LOAD(llama_init_from_model); LOAD(llama_free);
        LOAD(llama_model_get_vocab); LOAD(llama_model_n_embd); LOAD(llama_vocab_n_tokens);
        LOAD(llama_tokenize); LOAD(llama_batch_init); LOAD(llama_batch_free); LOAD(llama_decode);
        LOAD(llama_get_memory); LOAD(llama_memory_clear); LOAD(llama_get_embeddings_ith); LOAD(llama_get_logits_ith);
        f_llama_backend_init();
        auto mp = f_llama_model_default_params();
        mp.n_gpu_layers = 99;
        auto *model = f_llama_model_load_from_file(argv[2], mp);
        if (!model) throw std::runtime_error("Model load failed");
        const auto *vocab = f_llama_model_get_vocab(model);
        const int dim = f_llama_model_n_embd(model);
        if (dim != 2048) throw std::runtime_error("Expected Qwen hidden dimension 2048");
        auto cp = f_llama_context_default_params();
        cp.n_ctx = 1024; cp.n_batch = 512; cp.n_ubatch = 512; cp.n_seq_max = 1;
        cp.n_threads = 8; cp.n_threads_batch = 8;
        cp.embeddings = false; cp.pooling_type = LLAMA_POOLING_TYPE_NONE;
        auto *chat = f_llama_init_from_model(model, cp);
        cp.n_ctx = 512; cp.embeddings = true;
        auto *features = f_llama_init_from_model(model, cp);
        if (!chat || !features) throw std::runtime_error("Context creation failed");
        auto tokenize = [&](const std::string &text) {
            int n = f_llama_tokenize(vocab, text.data(), (int) text.size(), nullptr, 0, true, true);
            if (n >= 0) throw std::runtime_error("Unexpected tokenizer sizing result");
            std::vector<llama_token> ids(-n);
            n = f_llama_tokenize(vocab, text.data(), (int) text.size(), ids.data(), (int) ids.size(), true, true);
            if (n <= 0 || n > 500) throw std::runtime_error("Prompt exceeds pilot token budget");
            ids.resize(n);
            return ids;
        };
        auto decode = [&](llama_context *ctx, const std::vector<llama_token> &ids, int offset) {
            auto b = f_llama_batch_init((int) ids.size(), 0, 1);
            b.n_tokens = (int) ids.size();
            for (int i = 0; i < b.n_tokens; ++i) {
                b.token[i] = ids[i]; b.pos[i] = offset+i; b.n_seq_id[i] = 1; b.seq_id[i][0] = 0;
                b.logits[i] = i == b.n_tokens-1;
            }
            int result = f_llama_decode(ctx, b);
            f_llama_batch_free(b);
            if (result != 0) throw std::runtime_error("llama_decode failed: " + std::to_string(result));
        };
        auto encode = [&](const std::string &text) {
            // Contract v1: raw final-layer vector at the last token of this exact template.
            auto ids = tokenize("Motion description: " + text + "\nRepresentation:");
            f_llama_memory_clear(f_llama_get_memory(features), true);
            decode(features, ids, 0);
            auto *ptr = f_llama_get_embeddings_ith(features, -1);
            if (!ptr) throw std::runtime_error("Embedding output was not allocated");
            std::vector<float> vector(ptr, ptr+dim);
            for (float v : vector) if (!std::isfinite(v)) throw std::runtime_error("Nonfinite embedding");
            return vector;
        };
        auto chat_sample = [&](bool interleave) {
            auto ids = tokenize("<|im_start|>user\nReply with a short greeting.<|im_end|>\n<|im_start|>assistant\n");
            f_llama_memory_clear(f_llama_get_memory(chat), true);
            decode(chat, ids, 0);
            std::vector<int> result;
            for (int i=0; i<8; ++i) {
                if (interleave) encode("A person raises the right hand slowly.");
                const float *logits = f_llama_get_logits_ith(chat, -1);
                if (!logits) throw std::runtime_error("Chat logits missing");
                int token = (int) (std::max_element(logits, logits+f_llama_vocab_n_tokens(vocab))-logits);
                result.push_back(token);
                decode(chat, std::vector<llama_token>{token}, (int) ids.size()+i);
            }
            return result;
        };
        const auto baseline_chat = chat_sample(false);
        const auto interleaved_chat = chat_sample(true);
        const auto first = encode("A person waves the right hand slowly.");
        encode("A person bows forward quickly.");
        const auto repeat = encode("A person waves the right hand slowly.");
        float repeat_error = 0;
        for (int i=0; i<dim; ++i) repeat_error = std::max(repeat_error, std::abs(first[i]-repeat[i]));
        if (baseline_chat != interleaved_chat || repeat_error > 1e-4f)
            throw std::runtime_error("Context isolation/repeatability check failed");
        std::ifstream input(argv[3]);
        std::ofstream output(argv[4], std::ios::binary);
        if (!input || !output) throw std::runtime_error("Cannot open prompt/output file");
        std::string line;
        size_t count = 0;
        std::vector<double> timings;
        while (std::getline(input, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) throw std::runtime_error("Empty prompt line");
            auto start = std::chrono::steady_clock::now();
            auto values = encode(line);
            timings.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now()-start).count());
            output.write(reinterpret_cast<const char *>(values.data()), dim*sizeof(float));
            ++count;
            if (count % 100 == 0) std::cerr << "Encoded " << count << " prompts\n";
        }
        if (!count || !output.good()) throw std::runtime_error("No features or output write failure");
        output.close();
        std::sort(timings.begin(), timings.end());
        std::ofstream report(argv[5]);
        report << "{\"schema\":1,\"feature_contract\":\"qwen-motion-raw-last-v1\","
               << "\"model_load_count\":1,\"context_count\":2,\"dimension\":" << dim
               << ",\"rows\":" << count << ",\"chat_interleave_equal\":true,\"repeat_max_abs\":" << repeat_error
               << ",\"encode_p50_ms\":" << timings[count/2]
               << ",\"encode_p95_ms\":" << timings[std::min(count-1, (size_t) std::floor(count*.95))] << "}\n";
        if (!report.good()) throw std::runtime_error("Report write failed");
        f_llama_free(features); f_llama_free(chat); f_llama_model_free(model); f_llama_backend_free();
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
