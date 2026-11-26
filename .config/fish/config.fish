# ============================================================
# ENVIRONMENT
# ============================================================

set -gx EDITOR nvim
set -gx VISUAL nvim


if status is-interactive

    set -g fish_greeting

    # ========================================================
    # STARSHIP
    # ========================================================

    if type -q starship
        starship init fish | source
    end


    # ========================================================
    # YAZI
    #
    # Lets Yazi change the current Fish directory when exiting.
    # ========================================================

    function y
        set tmp (mktemp -t "yazi-cwd.XXXXXX")
        yazi $argv --cwd-file="$tmp"

        if read -z cwd < "$tmp"; and [ -n "$cwd" ]; and [ "$cwd" != "$PWD" ]
            builtin cd -- "$cwd"
        end

        rm -f -- "$tmp"
    end


    # ========================================================
    # GENERAL
    # ========================================================

    function d
        cd /mnt/c/Users/Matthew/Downloads || return
        y
    end

    function personal
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/personal || return
        y
    end

    function cfg
        cd /mnt/c/Users/Matthew/Personal/dotfiles || return
        y
    end

    function fi-cfg
        cd ~/.config/fish || return
        y
    end


    # ========================================================
    # UCSD NOTES
    # ========================================================

    function notes
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes || return
        y
    end

    function fourth
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year || return
        y
    end

    function book
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/books || return
        y
    end


    # ========================================================
    # CURRENT QUARTER
    #
    # n
    #   -> opens Fall 2026
    #
    # n math180a
    #   -> opens/creates:
    #      fa2026/math180a/notes
    # ========================================================

    function n
        set base /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year/fa2026

        if test (count $argv) -eq 0
            cd "$base" || return
            y
            return
        end

        set notes_dir "$base/$argv[1]/notes"

        mkdir -p "$notes_dir" || return

        cd "$notes_dir" || return
        y
    end


    # ========================================================
    # CURRENT CLASSES
    # ========================================================

    function 103a
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year/fa2026/math103a || return
        y
    end

    function 180a
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year/fa2026/math180a || return
        y
    end

    function cogs1
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year/fa2026/cogs1 || return
        y
    end

    function 14a
        cd /mnt/c/Users/Matthew/Personal/school/ucsd-notes/4th-year/fa2026/cogs14a || return
        y
    end


    # ========================================================
    # PRACTICE
    #
    # p
    #   -> /mnt/c/Users/Matthew/Personal/school/ucsd-notes/personal/practice
    #
    # p cpp
    #   -> /mnt/c/Users/Matthew/Personal/school/ucsd-notes/personal/practice/cpp
    # ========================================================

    function p
        set base /mnt/c/Users/Matthew/Personal/school/ucsd-notes/personal/practice

        if test (count $argv) -eq 0
            mkdir -p "$base" || return
            cd "$base" || return
        else
            set target "$base/$argv[1]"
            mkdir -p "$target" || return
            cd "$target" || return
        end

        y
    end


    # ========================================================
    # TMUX
    #
    # Example:
    #   t school
    #
    # Creates the session if necessary or attaches if it exists.
    # ========================================================

    function t
        if test (count $argv) -eq 0
            echo "usage: t <session-name>"
            return 1
        end

        tmux new-session -A -s $argv[1]
    end

end
