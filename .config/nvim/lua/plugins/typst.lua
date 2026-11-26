return {
    {
        "chomosuke/typst-preview.nvim",

        ft = "typst",

        opts = function()
            local tinymist = vim.fn.exepath("tinymist")
            return {
                dependencies_bin = {
                    tinymist = tinymist ~= "" and tinymist or nil,
                },
            }
        end,

        config = function(_, opts)
            require("typst-preview").setup(opts)

            -- =================================================
            -- TYPST KEYBINDS
            -- =================================================

            vim.api.nvim_create_autocmd("FileType", {
                pattern = "typst",

                callback = function(args)

                    -- -------------------------------------------------
                    -- Format
                    -- Space + f
                    -- -------------------------------------------------

                    vim.keymap.set("n", "<leader>f", function()
                        vim.lsp.buf.format({
                            async = true,
                        })
                    end, {
                        buffer = args.buf,
                        desc = "Format Typst",
                    })


                    -- =================================================
                    -- TYPST PREVIEW
                    --
                    -- Space + t + p
                    -- Opens in the default browser
                    -- =================================================

                    vim.keymap.set("n", "<leader>tp", function()
                        vim.cmd("TypstPreviewToggle")
                    end, {
                        buffer = args.buf,
                        desc = "Toggle Typst Preview",
                    })

                    -- -------------------------------------------------
                    -- Export PDF
                    -- Space + t + o
                    -- -------------------------------------------------

                    vim.keymap.set("n", "<leader>to", function()
                        local source = vim.api.nvim_buf_get_name(args.buf)
                        if source == "" then
                            vim.notify("Save the Typst file before exporting", vim.log.levels.WARN)
                            return
                        end

                        if vim.fn.executable("typst") ~= 1 then
                            vim.notify("Typst CLI was not found in PATH", vim.log.levels.ERROR)
                            return
                        end

                        vim.api.nvim_buf_call(args.buf, function()
                            vim.cmd("silent write")
                        end)

                        vim.system({ "typst", "compile", source }, { text = true }, function(result)
                            vim.schedule(function()
                                if result.code == 0 then
                                    local pdf = vim.fn.fnamemodify(source, ":r") .. ".pdf"
                                    vim.notify("Created " .. pdf, vim.log.levels.INFO)
                                else
                                    local message = (result.stderr or ""):gsub("%s+$", "")
                                    vim.notify(message ~= "" and message or "Typst PDF export failed", vim.log.levels.ERROR)
                                end
                            end)
                        end)
                    end, {
                        buffer = args.buf,
                        desc = "Export Typst PDF",
                    })
                end,
            })
        end,
    },
}
