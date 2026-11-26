return {
    {
        "nvim-treesitter/nvim-treesitter",
        lazy = false,
        build = ":TSUpdate",

        config = function()
            local ts = require("nvim-treesitter")

            -- Install parsers we want
            ts.install({
                "lua",
                "vim",
                "vimdoc",
                "markdown",
                "markdown_inline",
                "typst",
            })

            -- Enable Treesitter automatically
            vim.api.nvim_create_autocmd("FileType", {
                pattern = {
                    "lua",
                    "vim",
                    "markdown",
                    "typst",
                },

                callback = function(args)
                    local filetype = vim.bo[args.buf].filetype
                    if vim.treesitter.language.add(filetype) then
                        vim.treesitter.start(args.buf, filetype)
                    end
                end,
            })
        end,
    },
}
